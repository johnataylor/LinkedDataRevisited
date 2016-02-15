using JsonLD.Core;
using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog.JsonLDIntegration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace XmlLegacy
{
    public class Common
    {
        public static IGraph GraphFromXml(XDocument original, XslCompiledTransform transform, XsltArgumentList arguments)
        {
            XDocument rdfxml = new XDocument();
            using (XmlWriter writer = rdfxml.CreateWriter())
            {
                transform.Transform(original.CreateReader(), arguments, writer);
            }

            RdfXmlParser rdfXmlParser = new RdfXmlParser();
            XmlDocument doc = new XmlDocument();
            doc.Load(rdfxml.CreateReader());

            //DEBUG
            //using (var xmlWriter = XmlWriter.Create(Console.Out))
            //{
            //    doc.WriteTo(xmlWriter);
            //}

            IGraph graph = new Graph();
            rdfXmlParser.Load(graph, doc);

            return graph;
        }

        public static JObject JsonFromGraph(IGraph graph, string rootType, JToken context)
        {
            System.IO.StringWriter stringWriter = new System.IO.StringWriter();
            IRdfWriter rdfWriter = new JsonLdWriter();
            rdfWriter.Save(graph, stringWriter);
            stringWriter.Flush();

            JObject frame = new JObject();
            frame.Add("@context", context);
            frame.Add("@type", rootType);
            //frame.Add("@embed", false);

            JToken flattened = JToken.Parse(stringWriter.ToString());
            JObject framed = JsonLdProcessor.Frame(flattened, frame, new JsonLdOptions());
            JObject compacted = JsonLdProcessor.Compact(framed, context, new JsonLdOptions());

            return compacted;
        }
        public static IGraph GraphFromJson(JToken compacted)
        {
            JToken flattened = JsonLdProcessor.Flatten(compacted, new JsonLdOptions());

            IRdfReader rdfReader = new JsonLdReader();
            IGraph graph = new Graph();
            rdfReader.Load(graph, new StringReader(flattened.ToString()));

            return graph;
        }
        public static void Reverse(IGraph graph, Uri existingProperty, Uri reverseProperty)
        {
            var existingTriples = graph
                .GetTriplesWithPredicate(graph.CreateUriNode(existingProperty))
                .ToList();

            graph.Retract(existingTriples);

            INode predicate = graph.CreateUriNode(reverseProperty);
            foreach (var existingTriple in existingTriples)
            {
                graph.Assert(existingTriple.Object, predicate, existingTriple.Subject);
            }
        }

        public static JToken JsonFromGraph2(IGraph graph, string rootType, JToken context)
        {
            System.IO.StringWriter stringWriter = new System.IO.StringWriter();
            IRdfWriter rdfWriter = new JsonLdWriter();
            rdfWriter.Save(graph, stringWriter);
            stringWriter.Flush();

            JToken flattened = JToken.Parse(stringWriter.ToString());

            return flattened;
        }

        public static void ApplyInference(IGraph graph, IGraph schema)
        {
            string inverseOf = @"
                PREFIX owl: <http://www.w3.org/2002/07/owl#>
                CONSTRUCT { ?y ?q ?x }
                WHERE { ?p owl:inverseOf ?q .
                        ?x ?p ?y . }
            ";

            var parser = new SparqlQueryParser();

            var rules = new List<SparqlQuery>();
            rules.Add(parser.ParseFromString(inverseOf));

            var store = new TripleStore();
            store.Add(graph, true);
            store.Add(schema, true);

            var queryProcessor = new LeviathanQueryProcessor(store);

            while (true)
            {
                int before = store.Triples.Count();

                foreach (var rule in rules)
                {
                    IGraph inferred = (IGraph)queryProcessor.ProcessQuery(rule);
                    //store.Add(inferred);
                    graph.Merge(inferred);
                }

                int after = store.Triples.Count();

                if (after == before)
                {
                    break;
                }
            }
        }
    }
}
