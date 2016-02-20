using Newtonsoft.Json.Linq;
using VDS.RDF;

namespace TransformWebApplication
{
    public class MergeResult
    {
        public IGraph Graph { get; set; }
        public string JsonLdFrame { get; set; }
        public JObject JsonLdContext { get; set; }
    }
}