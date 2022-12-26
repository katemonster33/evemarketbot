using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace EveMarketBot.EvePraisalApi
{
    public class StructuredResponse
    {
        public Appraisal appraisal { get; set; } = new Appraisal();
    }
}
