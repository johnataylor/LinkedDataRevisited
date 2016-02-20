using Microsoft.Owin;
using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog.JsonLDIntegration;
using Owin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Query.Datasets;
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

                else if (context.Request.Uri.PathAndQuery.StartsWith("/fetch"))
                {
                    string[] fields = context.Request.Uri.PathAndQuery.Split('/');
                    string content = await LoadMetadata(fields[2], fields[3]);
                    await context.Response.WriteAsync(content);
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
                case "eval":
                    await Eval(context);
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
            var result = await MergeImpl(context);

            JObject json = Common.JsonFromGraph(result.Graph, result.JsonLdFrame, result.JsonLdContext);

            await context.Response.WriteAsync(json.ToString());

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        async Task<MergeResult> MergeImpl(IOwinContext context)
        {
            var content = new StreamContent(context.Request.Body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);

            var result = new MergeResult();
            result.Graph = new Graph();

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

                        if (result.JsonLdContext == null)
                        {
                            result.JsonLdContext = (JObject)jsonLd["@context"];
                            result.JsonLdFrame = (string)jsonLd["@type"];
                        }

                        IGraph graph = Common.GraphFromJson(jsonLd);

                        result.Graph.Merge(graph, false);
                    }
                }
            }

            return result;
        }

        async Task Create(IOwinContext context)
        {
            await context.Response.WriteAsync("hello");

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        async Task Eval(IOwinContext context)
        {
            string path = context.Request.Uri.PathAndQuery;

            string[] fields = path.Split('/');
            string gistId = fields[2];

            var result = await MergeImpl(context);

            var rules = await LoadRules(gistId);

            EvalImpl(rules, result.Graph);

            JObject json = Common.JsonFromGraph(result.Graph, result.JsonLdFrame, result.JsonLdContext);

            await context.Response.WriteAsync(json.ToString());

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        void EvalImpl(List<string> rules, IGraph instance)
        {
            TripleStore store = new TripleStore();
            store.Add(instance, true);

            int before = 0;
            int after = 0;

            do
            {
                before = store.Triples.Count();

                foreach (string rule in rules)
                {
                    EvaluateRule(store, rule);
                }

                after = store.Triples.Count();
            }
            while (after > before);
        }

        static object Execute(TripleStore store, string sparql)
        {
            InMemoryDataset ds = new InMemoryDataset(store);
            ISparqlQueryProcessor processor = new LeviathanQueryProcessor(ds);
            SparqlQueryParser sparqlparser = new SparqlQueryParser();
            SparqlQuery query = sparqlparser.ParseFromString(sparql);
            return processor.ProcessQuery(query);
        }

        static void EvaluateRule(TripleStore store, string rule)
        {
            IGraph graph = (IGraph)Execute(store, rule);
            store.Add(graph, true);
        }

        async Task<List<string>> LoadRules(string gistId)
        {
            var obj = await LoadGistMetadata(gistId);

            var result = new List<string>();

            HttpClient client = new HttpClient();
            foreach (JProperty property in obj["files"])
            {
                string rawUrl = (string)property.Value["raw_url"];

                var content = await client.GetStringAsync(rawUrl);
                result.Add(content);
            }

            return result;
        }

        async Task<string> LoadMetadata(string gistId, string fileName)
        {
            var obj = await LoadGistMetadata(gistId);

            var fileInfo = obj["files"][fileName];
            if (fileInfo == null)
            {
                throw new Exception(string.Format("unable to find file {0}", fileName));
            }

            string rawUrl = (string)fileInfo["raw_url"];

            HttpClient client = new HttpClient();
            var content = await client.GetStringAsync(rawUrl);
            return content;
        }

        async Task<JObject> LoadGistMetadata(string gistId)
        {
            const string gistsBaseAddress = "https://api.github.com/gists/";

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TransformWebApplication", "1.0.0"));

            string address = string.Format("{0}{1}", gistsBaseAddress, gistId);

            var response = await client.GetAsync(address);

            //TODO: better error handling that just throwing an exception
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(json);

            return obj;
        }
    }
}