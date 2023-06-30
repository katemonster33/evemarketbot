using ESI.NET;
using ESI.NET.Enumerations;
using ESI.NET.Models.Market;
using ESI.NET.Models.SSO;
using EveMarketBot;
using EveMarketBot.Adam4EveApi;
using EveMarketBot.EvePraisalApi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.SQLite;
using System.Diagnostics;
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
        const int essenceRegionId = 10000064;
        const long heydielesHqId = 1039723362469;
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
                    string responseString = "<HTML><BODY> Congratulations! You've logged in! EveMarketBot liked that!</BODY></HTML>";
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

        async Task<string[]> IfNotFileExistsDownload(HttpClient client, string fileName)
        {
            if (!File.Exists(Environment.CurrentDirectory + fileName))
            {
                HttpResponseMessage? req = await client.GetAsync("/MarketPricesRegionHistory/2023" + fileName);
                if (req == null || req.StatusCode != HttpStatusCode.OK)
                {
                    return new string[0];
                }
                File.WriteAllBytes(Environment.CurrentDirectory + fileName, await req.Content.ReadAsByteArrayAsync());
            }
            return File.ReadAllLines(Environment.CurrentDirectory + fileName);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Dictionary<int, StockedItem> requestedItems = GetManufacturableItemsFromSde();
            //List<StockList> stockLists = GetStockListsFromJson(out DateTime lastStockRequestTime);
            DateTime nextStockRequestTime = DateTime.Now;
            DateTime nextHistoryReqStartTime = DateTime.Now;
            var authedCharacter = await AuthorizeSSO(esiClient);
            if (authedCharacter == null)
            {
                Console.WriteLine("Failed to authorize SSO!");
                return;
            }
            TimeSpan oneHourTimeSpan = new TimeSpan(1, 0, 0);
            TimeSpan oneDayTimeSpan = new TimeSpan(1, 0, 0, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                if(DateTime.Now >= nextHistoryReqStartTime)
                {
                    nextHistoryReqStartTime += oneDayTimeSpan;
                    DateTime rateLimitExpireTime = DateTime.Now + new TimeSpan(0, 1, 0);
                    int numRequests = 0;
                    foreach (var reqItem in requestedItems.Values)
                    {
                        var resp = await esiClient.Market.TypeHistoryInRegion(essenceRegionId, reqItem.TypeId);
                        numRequests++;
                        WaitIfRateLimited(ref numRequests, ref rateLimitExpireTime);
                        if (resp.StatusCode == HttpStatusCode.InternalServerError)
                        {
                            // rate limited? wait a minute
                            await Task.Delay(61000);
                            resp = await esiClient.Market.TypeHistoryInRegion(essenceRegionId, reqItem.TypeId);
                            numRequests++;
                            WaitIfRateLimited(ref numRequests, ref rateLimitExpireTime);
                        }
                        if(resp.StatusCode == HttpStatusCode.OK)
                        {
                            var marketStats = resp.Data.OrderByDescending(dd => dd.Date).ToList();
                            long weekVolume = 0;
                            for(int i =0; i < 7 && i < marketStats.Count; i++)
                            {
                                weekVolume += marketStats[i].Volume;
                            }
                            reqItem.VolumePerDay = weekVolume / 7;
                        }
                        else if(resp.StatusCode == HttpStatusCode.NotFound)
                        {
                            continue;
                        }
                        else
                        {
                            await Task.Delay(60000, cancellationToken);
                        }
                        reqItem.VolumePerDay = 0;
                    }
                }
                if (DateTime.Now >= nextStockRequestTime)
                {
                    await ReadJitaPricesFromAdam4Eve(requestedItems);
                    nextStockRequestTime = DateTime.Now + oneHourTimeSpan;
                    StockList stockList = new StockList() { Timestamp = DateTime.Now };

                    SsoToken token = await esiClient.SSO.GetToken(GrantType.RefreshToken, authedCharacter.RefreshToken);
                    authedCharacter = await esiClient.SSO.Verify(token);
                    esiClient.SetCharacterData(authedCharacter);

                    var orders = await ReadOrdersFromStructure(requestedItems, heydielesHqId);
                    if (orders == null)
                    {
                        Console.WriteLine("Failed to query orders");
                        Thread.Sleep(10000);
                        continue;
                    }
                    foreach (var structOrder in orders)
                    {
                        if (!structOrder.IsBuyOrder)
                        {
                            if (requestedItems.TryGetValue(structOrder.TypeId, out StockedItem? stockedItem) && stockedItem != null)
                            {
                                if (structOrder.Price < stockedItem.MinimumPrice)
                                {
                                    stockedItem.MinimumPrice = structOrder.Price;
                                }
                                int totalStock = structOrder.VolumeRemain;
                                if (stockList.StockCountsByTypeId.TryGetValue(structOrder.TypeId, out var stockCount))
                                {
                                    totalStock += stockCount;
                                }
                                stockList.StockCountsByTypeId[structOrder.TypeId] = totalStock;
                            }
                        }
                    }
                    WriteMarketReport(requestedItems, stockList);
                    //AddNewStockList(requestedItems, stockLists, threeDayTimeSpan, stockList);
                    //if (stockLists.Count > 1)
                    //{
                    //    CalculateEstimatedTradingVolume(requestedItems, stockLists);

                    //    WriteMarketReport(requestedItems, stockList);
                    //}
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

        private static void CalculateEstimatedTradingVolume(Dictionary<int, StockedItem> requestedItems, List<StockList> stockLists)
        {
            for (int i = 1; i < stockLists.Count; i++)
            {
                foreach (var stockItem in stockLists[i - 1].StockCountsByTypeId)
                {
                    int newStock = 0;
                    if (stockLists[i].StockCountsByTypeId.TryGetValue(stockItem.Key, out var newStockTmp))
                    {
                        newStock = newStockTmp;
                    }
                    int stockDelta = stockItem.Value - newStock; // old value minus new value = amount sold
                    if (stockDelta > 0 && requestedItems.TryGetValue(stockItem.Key, out var stockedItem))
                    {
                        stockedItem.VolumePerDay += stockDelta;
                    }
                }
            }
            TimeSpan dataTimeLength = DateTime.Now.Subtract(stockLists.Min(sl => sl.Timestamp));
            double divisor = new TimeSpan(1, 0, 0, 0) / dataTimeLength; // 24 hours divided by the length of time we have recorded equals the amount we need to multiply each data sample by in order to get # sold per day
            foreach (var reqItem in requestedItems.Values)
            {
                reqItem.VolumePerDay = (long)(reqItem.VolumePerDay * divisor);
            }
        }

        void AddNewStockList(Dictionary<int, StockedItem> requestedItems, List<StockList> stockLists, TimeSpan threeDayTimeSpan, StockList stockList)
        {
            stockLists.Add(stockList);
            foreach (var stockItem in stockList.StockCountsByTypeId)
            {
                if (requestedItems.TryGetValue(stockItem.Key, out var stockedItem))
                {
                    stockedItem.CurrentStock = stockItem.Value;
                }
            }
            for (int i = 0; i < stockLists.Count;)
            {
                if (DateTime.Now.Subtract(stockLists[i].Timestamp) > threeDayTimeSpan)
                {
                    stockLists.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
            File.WriteAllText(stockListsJsonPath, JsonSerializer.Serialize(stockLists));
        }

        List<StockList> GetStockListsFromJson(out DateTime lastStockRequestTime)
        {
            var stockLists = new List<StockList>();
            lastStockRequestTime = DateTime.MinValue;
            if (File.Exists(stockListsJsonPath))
            {
                stockLists = JsonSerializer.Deserialize<List<StockList>>(File.ReadAllText(stockListsJsonPath)) ?? new List<StockList>();
                lastStockRequestTime = stockLists.Select(sl => sl.Timestamp).Max();
            }
            return stockLists;
        }

        private void WriteMarketReport(Dictionary<int, StockedItem> requestedItems, StockList stockList)
        {
            List<string[]> csvLines = new List<string[]>();
            csvLines.Add(new string[] { "typeid", "name", "heyd_price", "jita_price", "heyd_stock", "stockvelocity", "saturation", "markup" });
            foreach (var stockedItem in requestedItems.Values)
            {
                if (stockedItem.CurrentStock > 0 && stockedItem.JitaPrice > 0)
                {
                    decimal saturation = stockedItem.VolumePerDay / (decimal)stockedItem.CurrentStock;
                    decimal markup = (stockedItem.MinimumPrice / (decimal)stockedItem.JitaPrice) * 100 - 100;
                    csvLines.Add(new string[] { stockedItem.TypeId.ToString(), stockedItem.Name.ToString(), $"{stockedItem.MinimumPrice:0.00}", $"{stockedItem.JitaPrice:0.00}", stockedItem.CurrentStock.ToString(), stockedItem.VolumePerDay.ToString(), $"{saturation:0.0}", $"{markup:0.0}" });
                }
            }
            Logger.LogInformation("Synced orders @ " + stockList.Timestamp.ToLongDateString() + " " + stockList.Timestamp.ToLongTimeString());
            File.WriteAllLines("output.csv", csvLines.Select(tokens => string.Join(',', tokens)).ToArray());
        }

        private async Task<List<Order>?> ReadOrdersFromStructure(Dictionary<int, StockedItem> requestedItems, long structureId)
        {
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
                        Console.WriteLine("Waiting and repeating request...");
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

        private async Task ReadJitaPricesFromAdam4Eve(Dictionary<int, StockedItem> requestedItems)
        {
            using (HttpClient client = new())
            {
                client.BaseAddress = new Uri("https://static.adam4eve.eu");
                PopulateUserAgent(client.DefaultRequestHeaders);
                var dayTimeSpan = new TimeSpan(1, 0, 0, 0);
                DateTime currentDateTime = DateTime.Now.Subtract(dayTimeSpan);

                int i = 0;
                string fileName = $"/marketPrice_{theForgeRegionId}_daily_{currentDateTime:yyyy-MM-dd}.csv";
                string[] lines = await IfNotFileExistsDownload(client, fileName);
                for (i = 1; i < lines.Length; i++)
                {
                    MarketPriceRow prices = MarketPriceRow.CreateFromCsv(lines[i].Split(';'));

                    if (prices.SellPriceLow != null && requestedItems.TryGetValue(prices.TypeId, out var stockedItem))
                    {
                        stockedItem.JitaPrice = (double)(decimal)prices.SellPriceLow;
                    }
                }
                fileName = $"/marketPrice_{theForgeRegionId}_daily_{currentDateTime.Subtract(dayTimeSpan):yyyy-MM-dd}.csv";
                if (File.Exists(fileName)) File.Delete(fileName);
            }
        }

        private static Dictionary<int, StockedItem> GetManufacturableItemsFromSde()
        {
            const string itemsQuery = "SELECT invTypes.typeID,invTypes.typeName FROM invTypes INNER JOIN invGroups ON invGroups.groupID = invTypes.groupID INNER JOIN invCategories ON invCategories.categoryID = invGroups.categoryID WHERE (metaGroupID IS NULL OR metaGroupID NOT IN (3,4,5,6)) AND invGroups.categoryID NOT IN(2,9,11,17,23,30,41,63,65,66,87,91) AND invTypes.marketGroupID IS NOT NULL;";
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
                    requestedItems.Add(typeId, new StockedItem() { TypeId = typeId, Name = name, MinimumPrice = decimal.MaxValue, CurrentStock = 0, VolumePerDay = 0 });
                }
            }
            conn.Close();
            return requestedItems;
        }
    }
}