using Microsoft.Owin;
using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog.JsonLDIntegration;
using Owin;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using VDS.RDF;
using XmlLegacy;

[assembly: OwinStartup("TransformWebApplication", typeof(TransformWebApplication.Startup))]

namespace TransformWebApplication
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            app.Run(Invoke);
        }

        async Task Invoke(IOwinContext context)
        {
            switch (context.Request.Method)
            {
                case "GET":
                    await InvokeGET(context);
                    break;
                case "POST":
                    await InvokePOST(context);
                    break;
                default:
                    await context.Response.WriteAsync("NOT FOUND");
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    break;
            }
        }

        async Task InvokeGET(IOwinContext context)
        {
            try
            {
                if (context.Request.Uri.PathAndQuery == "/")
                {
                    await context.Response.WriteAsync("READY");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    return;
                }

                else
                {
                    string[] fields = context.Request.Uri.PathAndQuery.Split('/');
                    string content = await LoadMetadata(fields[1], fields[2]);
                    await context.Response.WriteAsync(content);
                    //await context.Response.WriteAsync("what the hell");
                    return;
                }

                await context.Response.WriteAsync("NOT FOUND");
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
            catch (Exception e)
            {
                await context.Response.WriteAsync(e.Message);
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
        }
        async Task InvokePOST(IOwinContext context)
        {
            //TODO: some basic error checking

            string[] fields = context.Request.Uri.PathAndQuery.Split('/');

            string function = fields[1];

            switch (function)
            {
                case "xml2json":
                    await Xml2Json(context);
                    break;
                case "merge":
                    await Merge(context);
                    break;
            }
        }

        async Task Xml2Json(IOwinContext context)
        {
            string[] fields = context.Request.Uri.PathAndQuery.Split('/');

            if (fields.Length != 6)
            {
                await context.Response.WriteAsync("xml2json requires github gistId, XSLT, JSON-LD context and JSON-LD framing type");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            string gistId = fields[2];
            string xsltName = fields[3];
            string jsonLdContextName = fields[4];
            string jsonLdFrameType = fields[5];

            string xsltText = await LoadMetadata(gistId, xsltName);
            string jsonLdContextText = await LoadMetadata(gistId, jsonLdContextName);

            XDocument original = XDocument.Load(context.Request.Body);

            XDocument styleSheet = XDocument.Parse(xsltText);

            JToken jsonLdContext = JObject.Parse(jsonLdContextText);

            XslCompiledTransform transform = new XslCompiledTransform();
            transform.Load(styleSheet.CreateReader());

            XsltArgumentList arguments = new XsltArgumentList();
            arguments.AddParam("baseAddress", "", "http://example.org/book/");

            IGraph graph = Common.GraphFromXml(original, transform, arguments);

            JObject jsonLd = Common.JsonFromGraph(graph, jsonLdFrameType, jsonLdContext);

            await context.Response.WriteAsync(jsonLd.ToString());
            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }
        async Task Merge(IOwinContext context)
        {
            var content = new StreamContent(context.Request.Body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);

            IGraph merged = new Graph();

            JObject jsonLdContext = null;
            string jsonLdFrame = null;

            var provider = await content.ReadAsMultipartAsync();
            foreach (var httpContent in provider.Contents)
            {
                var fileName = httpContent.Headers.ContentDisposition.FileName;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                using (Stream fileContent = await httpContent.ReadAsStreamAsync())
                {
                    using (TextReader reader = new StreamReader(fileContent))
                    {
                        string data = await reader.ReadToEndAsync();

                        JToken jsonLd = JToken.Parse(data);

                        jsonLdContext = (JObject)jsonLd["@context"];
                        jsonLdFrame = (string)jsonLd["@type"];

                        IGraph graph = Common.GraphFromJson(jsonLd);

                        merged.Merge(graph, false);
                    }
                }
            }

            JObject json = Common.JsonFromGraph(merged, jsonLdFrame, jsonLdContext);

            await context.Response.WriteAsync(json.ToString());

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        async Task<string> LoadMetadata(string gistId, string fileName)
        {
            const string gistsBaseAddress = "https://api.github.com/gists/";

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Xml2JsonLd", "1.0.0"));

            string address = string.Format("{0}{1}", gistsBaseAddress, gistId);

            var response = await client.GetAsync(address);

            //TODO: better error handling that just throwing an exception
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(json);

            var fileInfo = obj["files"][fileName];
            if (fileInfo == null)
            {
                throw new Exception(string.Format("unable to find file {0}", fileName));
            }

            string rawUrl = (string)fileInfo["raw_url"];
            var content = await client.GetStringAsync(rawUrl);
            return content;
        }
    }
}