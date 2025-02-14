using ESI.NET;
using ESI.NET.Enumerations;
using ESI.NET.Models.Market;
using ESI.NET.Models.SSO;
using EveMarketBot;
using EveMarketBot.Adam4EveApi;
using EveMarketBot.EvePraisalApi;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.SQLite;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;

namespace EveMarketBot
{
    public class Worker : BackgroundService
    {
        ILogger<Worker> Logger { get; set; }

        const int numRetries = 3;
        const int theForgeRegionId = 10000002;
        //const int essenceRegionId = 10000064;
        const int placidRegionId = 10000048;
        const long heydielesHqId = 1039723362469;
        const int vlillirierSystemId = 30003836;
        const string stockListsJsonPath = "stockLists.json";

        IEsiClient esiClient;
        public Worker(IEsiClient client, ILogger<Worker> logger)
        {
            esiClient = client;
            Logger = logger;
        }

        public static async Task<AuthorizedCharacterData?> AuthorizeSSO(IEsiClient esiClient)
        {
            const string callbackUrl = "http://localhost:8080/";
            var listener = new HttpListener();
            listener.Prefixes.Add(callbackUrl);
            listener.Start();
            string state = "BLAH";
            Process.Start(new ProcessStartInfo
            {
                FileName = esiClient.SSO.CreateAuthenticationUrl(new List<string>() { "publicData", "esi-markets.structure_markets.v1" }, state),
                UseShellExecute = true
            });
            bool runServer = true;

            string? ssoResponse = null;
            // While a user hasn't visited the `shutdown` url, keep on handling requests
            while (runServer)
            {
                // Will wait here until we hear from a connection
                HttpListenerContext ctx = await listener.GetContextAsync();

                // If `shutdown` url requested w/ POST, then shutdown the server after serving the page
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url?.AbsolutePath == "/callback")
                {
                    var pairs = HttpUtility.ParseQueryString(ctx.Request.Url.Query);
                    if(pairs != null) ssoResponse = pairs["code"];
                    string responseString = "<HTML><BODY> Congratulations! You've logged in! EveMarketBot liked that! You can close this tab.</BODY></HTML>";
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                    // Get a response stream and write the response to it.
                    ctx.Response.ContentLength64 = buffer.Length;
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    // You must close the output stream.
                    ctx.Response.OutputStream.Close();
                    break;
                }
            }

            listener.Stop();
            listener.Close();
            if (ssoResponse != null)
            {
                SsoToken token = await esiClient.SSO.GetToken(GrantType.AuthorizationCode, ssoResponse);
                AuthorizedCharacterData characterData = await esiClient.SSO.Verify(token);
                esiClient.SetCharacterData(characterData);
                return characterData;
            }
            return null;
        }

        public static void PopulateUserAgent(HttpRequestHeaders headers)
        {
            // Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/89.0.4389.72 Safari/537.36
            headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
            headers.UserAgent.Add(new ProductInfoHeaderValue("(Windows NT 10.0; Win64; x64)"));
            headers.UserAgent.Add(new ProductInfoHeaderValue("AppleWebKit", "537.36"));
            headers.UserAgent.Add(new ProductInfoHeaderValue("(KHTML, like Gecko)"));
            headers.UserAgent.Add(new ProductInfoHeaderValue("Chrome", "89.0.4389.72"));
            headers.UserAgent.Add(new ProductInfoHeaderValue("Safari", "537.36"));
        }

        async Task<string[]> DownloadAdam4EveMarketHistory(HttpClient client, int regionId, DateTime timeStamp)
        {
            string fileName = $"marketPrice_{regionId}_daily_{timeStamp:yyyy-MM-dd}.csv";
            HttpResponseMessage? req = await client.GetAsync($"/MarketPricesRegionHistory/{timeStamp:yyyy}/" + fileName);
            if (req == null || req.StatusCode != HttpStatusCode.OK)
            {
                Logger.LogError($"File {fileName} was not found!");
                return new string[0];
            }
            string output = await req.Content.ReadAsStringAsync();
            Logger.LogInformation($"GET {fileName} success: returned {output.Length} bytes");
            return output.Split('\n');
        }

        async Task<string?> IfNotKillmailDumpExistsDownload(DateTime utcDay)
        {
            string fileName = $"killmails-{utcDay.Year}-{utcDay.Month:d2}-{utcDay.Day}.tar.bz2";
           if (!File.Exists(Environment.CurrentDirectory + fileName))
           {
            HttpClient wc = new HttpClient();
            wc.BaseAddress = new Uri("https://data.everef.net");

            PopulateUserAgent(wc.DefaultRequestHeaders);

            DateTime yesterdayUtc = DateTime.UtcNow.AddDays(-1);
            var resp = await wc.GetAsync($"/killmails/{yesterdayUtc.Year}/{fileName}");
               if (resp == null || resp.StatusCode != HttpStatusCode.OK)
               {
                   Logger.LogError($"File {fileName} was not found!");
                   return null;
               }
               File.WriteAllBytes(Environment.CurrentDirectory + fileName, await resp.Content.ReadAsByteArrayAsync());
           }
           return Environment.CurrentDirectory + fileName;
        }

        async Task FetchKillmails(int regionId, Dictionary<int, int> solarSystemToRegionId, Dictionary<int, StockedItem> requestedItems)
        {
            DateTime yesterdayUtc = DateTime.UtcNow.AddDays(-1);
            string? fileName = await IfNotKillmailDumpExistsDownload(yesterdayUtc);
            if(fileName != null) {
                using(MemoryStream fs = new MemoryStream(File.ReadAllBytes(fileName)))
                using(BZip2InputStream bzipFile = new BZip2InputStream(fs))
                using(TarReader tr = new TarReader(bzipFile))
                {
                    System.Formats.Tar.TarEntry? entry = null;
                    while((entry = tr.GetNextEntry()) != null)
                    {
                        if(entry.DataStream != null)
                        {
                            using(JsonDocument jdoc = JsonDocument.Parse(entry.DataStream))
                            {
                                int kmRegionId = 0;
                                foreach(var prop in jdoc.RootElement.EnumerateObject()) {
                                    if(prop.Name == "solar_system_id") {
                                        int solarSysId = prop.Value.GetInt32();
                                        if(!solarSystemToRegionId.TryGetValue(solarSysId, out kmRegionId)) {
                                            kmRegionId = -1;
                                        }
                                    }
                                }
                                if(kmRegionId == regionId)
                                {
                                    foreach(var prop in jdoc.RootElement.EnumerateObject())
                                    {
                                        if(prop.Name == "victim") {
                                            foreach(var vicProp in prop.Value.EnumerateObject()) {
                                                if(vicProp.Name == "items") {
                                                    foreach(var item in vicProp.Value.EnumerateArray()) {
                                                        int itemTypeId = -1;
                                                        int itemQuantity = -1;
                                                        foreach(var itemProp in item.EnumerateObject()) {
                                                            switch(itemProp.Name)
                                                            {
                                                                case "item_type_id":
                                                                    itemTypeId = itemProp.Value.GetInt32();
                                                                    break;
                                                                case "quantity_destroyed":
                                                                    itemQuantity = itemProp.Value.GetInt32();
                                                                    break;
                                                            }
                                                        }
                                                        if(itemTypeId != -1 && itemQuantity != -1 && requestedItems.TryGetValue(itemTypeId, out var stockedItem)) {
                                                            requestedItems[itemTypeId].VolumePerDay += itemQuantity;
                                                        }
                                                    }
                                                } else if(vicProp.Name == "ship_type_id") {
                                                    var shipTypeId = vicProp.Value.GetInt32();
                                                    if(requestedItems.TryGetValue(shipTypeId, out var stockedItem)) {
                                                        stockedItem.VolumePerDay++;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var solarSysToRegion = GetSolarSystemRegionsFromSde();
            var typeNameConverter =GetItemNamesFromSde();
            //List<StockList> stockLists = GetStockListsFromJson(out DateTime lastStockRequestTime);
            DateTime nextStockRequestTime = DateTime.Now;
            DateTime nextHistoryReqStartTime = DateTime.Now;
            // var authedCharacter = await AuthorizeSSO(esiClient);
            // if (authedCharacter == null)
            // {
            //     Console.WriteLine("Failed to authorize SSO!");
            //     return;
            // }
            TimeSpan oneHourTimeSpan = new TimeSpan(1, 0, 0);
            TimeSpan oneDayTimeSpan = new TimeSpan(1, 0, 0, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                // if (DateTime.Now >= nextHistoryReqStartTime)
                // {
                //     Logger.LogInformation("Querying market history...");
                //     nextHistoryReqStartTime += oneDayTimeSpan;
                //     DateTime rateLimitExpireTime = DateTime.Now + new TimeSpan(0, 1, 0);
                //     int numRequests = 0;
                //     int curIndex = 0, numItems = requestedItems.Count;
                //     foreach (var reqItem in requestedItems.Values)
                //     {
                //         reqItem.VolumePerDay = 0;
                //     }
                //     foreach (var reqItem in requestedItems.Values)
                //     {
                //         Logger.LogInformation($"Querying item {reqItem.TypeId}/{reqItem.Name} | ({curIndex} out of {numItems})");
                //         curIndex++;
                //         var resp = await esiClient.Market.TypeHistoryInRegion(placidRegionId, reqItem.TypeId);
                //         numRequests++;
                //         WaitIfRateLimited(ref numRequests, ref rateLimitExpireTime);
                //         if (resp.StatusCode == HttpStatusCode.InternalServerError)
                //         {
                //             Logger.LogWarning($"Rate limited! Waiting 61 seconds to prevent a ban.");
                //             // rate limited? wait a minute
                //             await Task.Delay(61000);
                //             resp = await esiClient.Market.TypeHistoryInRegion(placidRegionId, reqItem.TypeId);
                //             numRequests++;
                //             WaitIfRateLimited(ref numRequests, ref rateLimitExpireTime);
                //         }
                //         if (resp.StatusCode == HttpStatusCode.OK)
                //         {
                //             var marketStats = resp.Data.Where(dd => dd.Date > (DateTime.Now - new TimeSpan(10, 0, 0, 0))).OrderByDescending(dd => dd.Date).ToList();
                //             long weekVolume = 0;
                //             for (int i = 0; i < 7 && i < marketStats.Count; i++)
                //             {
                //                 weekVolume += marketStats[i].Volume;
                //             }
                //             reqItem.VolumePerDay = weekVolume / 7;
                //         }
                //         else if (resp.StatusCode == HttpStatusCode.NotFound)
                //         {
                //             Logger.LogWarning($"Server responded that the specified item does not exist.");
                //             continue;
                //         }
                //         else
                //         {
                //             Logger.LogWarning($"Got unexpected HTTP response {resp.StatusCode} when querying item");
                //             await Task.Delay(5000, cancellationToken);
                //         }
                //     }
                // }
                if (DateTime.Now >= nextStockRequestTime)
                {
                    DateTime rateLimitExpireTime = DateTime.Now + new TimeSpan(0, 1, 0);
                    Logger.LogInformation("Querying prices and orders...");
                    var requestedItems = await ReadJitaPricesFromAdam4Eve();
                    await FetchKillmails(10000048, solarSysToRegion, requestedItems);
                    nextStockRequestTime = DateTime.Now + oneHourTimeSpan;
                    try
                    {
                        var resp = await esiClient.Market.RegionOrders(placidRegionId, MarketOrderType.Sell, 1);
                        int numRequests = 1;
                        List<Order> allOrders = new List<Order>(resp.Data.Where(ord => ord.SystemId == vlillirierSystemId));
                        if(resp.StatusCode == HttpStatusCode.OK && resp.Pages != null && resp.Pages > 1) {
                            int numPages = (int)resp.Pages;
                            for(int pageId = 2; pageId < numPages; pageId++) {
                                WaitIfRateLimited(ref numRequests, ref rateLimitExpireTime);
                                resp = await esiClient.Market.RegionOrders(placidRegionId, MarketOrderType.Sell, pageId);
                                if(resp.StatusCode == HttpStatusCode.OK) {
                                    allOrders.AddRange(resp.Data.Where(ord => ord.SystemId == vlillirierSystemId));
                                } else if(resp.StatusCode == HttpStatusCode.InternalServerError) {
                                    Logger.LogWarning($"Rate limited! Waiting 61 seconds to prevent a ban.");
                                    // rate limited? wait a minute
                                    await Task.Delay(61000);
                                    pageId--;
                                    numRequests = 0;
                                    rateLimitExpireTime = new DateTime(0, 1, 0);
                                } else {
                                    Logger.LogWarning($"Got unexpected HTTP response {resp.StatusCode} when querying market orders");
                                    await Task.Delay(5000, cancellationToken);
                                    pageId--;
                                }
                                numRequests++;
                            }
                        }
                        else
                        {
                            Logger.LogWarning($"Got unexpected HTTP response {resp.StatusCode} when querying item");
                            await Task.Delay(5000, cancellationToken);
                        }
                        foreach(var stockedItem in requestedItems.Values) {
                            stockedItem.CurrentStock = 0;
                            stockedItem.LocalPrice = decimal.MaxValue;
                        }
                        foreach(var ord in allOrders) {
                            if(requestedItems.TryGetValue( ord.TypeId, out var stockedItem)){
                                stockedItem.CurrentStock += ord.VolumeRemain;
                                if(ord.Price < stockedItem.LocalPrice) {
                                    stockedItem.LocalPrice = ord.Price;
                                }
                            }
                        }
                        foreach(var stockedItem in requestedItems.Values) {
                            if(stockedItem.LocalPrice == decimal.MaxValue) {
                                stockedItem.LocalPrice = 0;
                            }
                        }
                        //await esiClient.Market.TypeHistoryInRegion()
                        //StockList stockList = new StockList() { Timestamp = DateTime.Now };

                        // SsoToken token = await esiClient.SSO.GetToken(GrantType.RefreshToken, authedCharacter.RefreshToken);
                        // authedCharacter = await esiClient.SSO.Verify(token);
                        // esiClient.SetCharacterData(authedCharacter);

                        // var orders = await ReadOrdersFromStructure(requestedItems, heydielesHqId);
                        // if (orders == null)
                        // {
                        //     Logger.LogWarning("Failed to query structure orders");
                        //     Thread.Sleep(10000);
                        //     nextStockRequestTime = DateTime.Now;
                        //     continue;
                        // }
                        // foreach (var structOrder in orders)
                        // {
                        //     if (!structOrder.IsBuyOrder)
                        //     {
                        //         if (requestedItems.TryGetValue(structOrder.TypeId, out StockedItem? stockedItem) && stockedItem != null)
                        //         {
                        //             if (structOrder.Price < stockedItem.LocalPrice)
                        //             {
                        //                 stockedItem.LocalPrice = structOrder.Price;
                        //             }
                        //             int totalStock = structOrder.VolumeRemain;
                        //             stockedItem.CurrentStock += totalStock;
                        //             //if (stockList.StockCountsByTypeId.TryGetValue(structOrder.TypeId, out var stockCount))
                        //             //{
                        //             //    totalStock += stockCount;
                        //             //}
                        //             //stockList.StockCountsByTypeId[structOrder.TypeId] = totalStock;
                        //         }
                        //     }
                        // }
                        //foreach (var stockItem in stockList.StockCountsByTypeId)
                        //{
                        //  if (requestedItems.TryGetValue(stockItem.Key, out var stockedItem))
                        //  {
                        //      stockedItem.CurrentStock = stockItem.Value;
                        //  }
                        //}
                        foreach(var item in requestedItems) {
                            if(typeNameConverter.TryGetValue(item.Key, out var typeName)) {
                                item.Value.Name = typeName;
                            }
                        }
                        WriteMarketReport(requestedItems);
                        //AddNewStockList(requestedItems, stockLists, threeDayTimeSpan, stockList);
                        //if (stockLists.Count > 1)
                        //{
                        //    CalculateEstimatedTradingVolume(requestedItems, stockLists);

                        //    WriteMarketReport(requestedItems, stockList);
                        //}
                    }
                    catch(HttpRequestException hre) {
                        Console.WriteLine("Cancelling request to update order sheet due to error when accessing ESI: " + hre.Message);
                        Thread.Sleep(61000);
                    }
                }
                await Task.Delay(10000, cancellationToken);
            }
        }

        void WaitIfRateLimited(ref int numRequests, ref DateTime rateLimitExpireTime)
        {
            if(numRequests >= 300 || DateTime.Now >= rateLimitExpireTime)
            {
                while (DateTime.Now <= rateLimitExpireTime)
                {
                    Thread.Sleep(1000);
                }
                rateLimitExpireTime = DateTime.Now + new TimeSpan(0, 1, 0);
                numRequests = 0;
            }
        }

        //private static void CalculateEstimatedTradingVolume(Dictionary<int, StockedItem> requestedItems, List<StockList> stockLists)
        //{
        //    for (int i = 1; i < stockLists.Count; i++)
        //    {
        //        foreach (var stockItem in stockLists[i - 1].StockCountsByTypeId)
        //        {
        //            int newStock = 0;
        //            if (stockLists[i].StockCountsByTypeId.TryGetValue(stockItem.Key, out var newStockTmp))
        //            {
        //                newStock = newStockTmp;
        //            }
        //            int stockDelta = stockItem.Value - newStock; // old value minus new value = amount sold
        //            if (stockDelta > 0 && requestedItems.TryGetValue(stockItem.Key, out var stockedItem))
        //            {
        //                stockedItem.VolumePerDay += stockDelta;
        //            }
        //        }
        //    }
        //    TimeSpan dataTimeLength = DateTime.Now.Subtract(stockLists.Min(sl => sl.Timestamp));
        //    double divisor = new TimeSpan(1, 0, 0, 0) / dataTimeLength; // 24 hours divided by the length of time we have recorded equals the amount we need to multiply each data sample by in order to get # sold per day
        //    foreach (var reqItem in requestedItems.Values)
        //    {
        //        reqItem.VolumePerDay = (long)(reqItem.VolumePerDay * divisor);
        //    }
        //}

        //void AddNewStockList(Dictionary<int, StockedItem> requestedItems, List<StockList> stockLists, TimeSpan threeDayTimeSpan, StockList stockList)
        //{
        //    stockLists.Add(stockList);
        //    for (int i = 0; i < stockLists.Count;)
        //    {
        //        if (DateTime.Now.Subtract(stockLists[i].Timestamp) > threeDayTimeSpan)
        //        {
        //            stockLists.RemoveAt(i);
        //        }
        //        else
        //        {
        //            i++;
        //        }
        //    }
        //    File.WriteAllText(stockListsJsonPath, JsonSerializer.Serialize(stockLists));
        //}

        //List<StockList> GetStockListsFromJson(out DateTime lastStockRequestTime)
        //{
        //    var stockLists = new List<StockList>();
        //    lastStockRequestTime = DateTime.MinValue;
        //    if (File.Exists(stockListsJsonPath))
        //    {
        //        stockLists = JsonSerializer.Deserialize<List<StockList>>(File.ReadAllText(stockListsJsonPath)) ?? new List<StockList>();
        //        lastStockRequestTime = stockLists.Select(sl => sl.Timestamp).Max();
        //    }
        //    return stockLists;
        //}

        private void WriteMarketReport(Dictionary<int, StockedItem> requestedItems)
        {
            List<string[]> csvLines = new List<string[]>();
            csvLines.Add(new string[] { "typeid", "name", "local_price", "jita_price", "current_stock", "volume_per_day"});
            foreach (var stockedItem in requestedItems.Values.Where(reqItem => reqItem.VolumePerDay > 0 && reqItem.JitaPrice > 0))
            {
                csvLines.Add(new string[] { stockedItem.TypeId.ToString(), stockedItem.Name.ToString(), $"{stockedItem.LocalPrice:0.00}", $"{stockedItem.JitaPrice:0.00}", stockedItem.CurrentStock.ToString(), stockedItem.VolumePerDay.ToString() });
            }
            Logger.LogInformation("Synced orders @ " + DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString());
            File.WriteAllLines("output.csv", csvLines.Select(tokens => string.Join(',', tokens)).ToArray());
        }

        private async Task<List<Order>?> ReadOrdersFromStructure(Dictionary<int, StockedItem> requestedItems, long structureId)
        {
            Logger.LogInformation("Reading market orders from structure...");
            int numPages = 2;
            List<Order> output = new List<Order>();
            EsiResponse<List<Order>>? structOrdersResp = null;
            for (int pageId = 1; pageId <= numPages; pageId++)
            {
                int retry = 0;
                for (; retry < numRetries; retry++)
                {
                    structOrdersResp = await esiClient.Market.StructureOrders(structureId, pageId);
                    if (structOrdersResp.StatusCode == HttpStatusCode.OK)
                    {
                        Thread.Sleep(100); // be nice to the server
                        break;
                    }
                    else
                    {
                        Logger.LogWarning("Waiting and repeating request...");
                        Thread.Sleep(500); // be extra nice to the server
                    }
                }
                if(retry >= numRetries || structOrdersResp == null)
                {
                    return null;
                }
                if (pageId == 1)
                {
                    numPages = structOrdersResp.Pages ?? 0;
                }                
                output.AddRange(structOrdersResp.Data);
            }
            return output;
        }

        async Task<Dictionary<int, StockedItem>> ReadJitaPricesFromAdam4Eve()
        {
            Dictionary<int, StockedItem> output = new Dictionary<int, StockedItem>();
            using (HttpClient client = new())
            {
                Logger.LogInformation("Querying jita prices from Adam4Eve...");
                client.BaseAddress = new Uri("https://static.adam4eve.eu");
                PopulateUserAgent(client.DefaultRequestHeaders);
                var dayTimeSpan = new TimeSpan(1, 0, 0, 0);
                DateTime currentDateTime = DateTime.Now.Subtract(dayTimeSpan);

                int i = 0;
                //string[] lines = await IfNotFileExistsDownload(client, fileName);
                string[] lines = await DownloadAdam4EveMarketHistory(client, theForgeRegionId, currentDateTime);
                for (i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i]))
                    {
                        continue;
                    }
                    MarketPriceRow prices = MarketPriceRow.CreateFromCsv(lines[i].Split(';'));

                    if (prices.SellPriceLow != null && !output.TryGetValue(prices.TypeId, out var stockedItem))
                    {
                        output[prices.TypeId] = new StockedItem(){TypeId = prices.TypeId, JitaPrice = (double)(decimal)prices.SellPriceLow };
                    }
                }
            }
            return output;
        }


        static Dictionary<int, string> GetItemNamesFromSde()
        {
            Dictionary<int, string> output = new();
            TextFieldParser parser = new TextFieldParser("invTypes.csv");
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",");
            parser.ReadLine();
            while(!parser.EndOfData)
            {
                string[]? fields = parser.ReadFields();
                if(fields != null && fields.Length > 8 && !string.IsNullOrEmpty(fields[0]))
                {
                    int typeId = int.Parse(fields[0]);
                    output[typeId] = fields[2];
                }
            }
            return output;
        }

        static Dictionary<int, int> GetSolarSystemRegionsFromSde()
        {
            Dictionary<int, int>output = new();
            TextFieldParser parser = new TextFieldParser("mapSolarSystems.csv");
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",");
            parser.ReadLine();
            while(!parser.EndOfData)
            {
                string[]? fields = parser.ReadFields();
                if(fields != null && fields.Length > 8 && !string.IsNullOrEmpty(fields[0]))
                {
                    int solarSysId = int.Parse(fields[2]);
                    int regionId = int.Parse(fields[0]);
                    output[solarSysId] = regionId;
                }
            }
            return output;
        }

        static Dictionary<int, StockedItem> GetManufacturableItemsFromSde()
        {
            const string itemsQuery = "SELECT invTypes.typeID,invTypes.typeName FROM " +
                "invTypes INNER JOIN invGroups ON invGroups.groupID = invTypes.groupID INNER JOIN invCategories ON invCategories.categoryID = invGroups.categoryID " +
                "WHERE " +
                    "(metaGroupID IS NULL OR metaGroupID NOT IN (3,4,5,6,19,17,52,53,54)) AND " +
                    "invGroups.categoryID NOT IN(2,9,11,16,17,23,30,41,63,65,66,87,91) AND " +
                    "invGroups.groupID NOT IN (485,513,547,659,30,833) AND " +
                    "invTypes.marketGroupID IS NOT NULL AND " +
                    "invTypes.published = 1;";
            const string dbPath = @"C:\Users\Kat\Downloads\sde\sde.sqlite";

            SQLiteConnection conn = new SQLiteConnection(new SQLiteConnectionStringBuilder() { DataSource = dbPath, ReadOnly = true }.ToString());

            conn.Open();
            Dictionary<int, StockedItem> requestedItems = new Dictionary<int, StockedItem>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = itemsQuery;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int typeId = reader.GetInt32(0);
                    string name = reader.GetString(1);
                    requestedItems.Add(typeId, new StockedItem() { TypeId = typeId, Name = name, LocalPrice = decimal.MaxValue, CurrentStock = 0, VolumePerDay = 0 });
                }
            }
            conn.Close();
            return requestedItems;
        }
    }
}