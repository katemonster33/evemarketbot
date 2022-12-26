using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace EveMarketBot.EvePraisalApi
{
    public class Appraisal
    {
        public long created { get; set; }

        public string id { get; set; } = string.Empty;

        public List<ItemResponse> items { get; set; } = new List<ItemResponse>();

        public string kind { get; set; } = string.Empty;

        public bool live { get; set; } = false;

        public string market_name { get; set; } = string.Empty;

        [JsonPropertyName("private")]
        public bool is_private { get; set; } = false;

        public string raw { get; set; } = string.Empty;

        public Totals totals { get; set; } = new Totals();
    }
}
