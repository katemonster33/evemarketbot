using ESI.NET;
using ESI.NET.Enumerations;
using ESI.NET.Models.Market;
using ESI.NET.Models.SSO;
using EveMarketBot;
using EveMarketBot.EvePraisalApi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;

namespace EveMarketBot
{
    public class Worker : IHostedService
    {
        ILogger<Worker> Logger { get; set; }
        const string itemsQuery = "SELECT DISTINCT invTypes.typeID,invTypes.typeName FROM invTypes INNER JOIN invGroups ON invGroups.groupID = invTypes.groupID INNER JOIN invCategories ON invCategories.categoryID = invGroups.categoryID WHERE metaGroupID IN(1,2,14) AND invGroups.categoryID NOT IN(2,9,11,17,23,25,30,41,63,65,66,87,91) AND invTypes.marketGroupID IS NOT NULL;";
        const string dbPath = @"C:\Users\Kat\Downloads\sde\sde.sqlite";

        const int essenceRegionId = 10000064;

        const long heydielesHqId = 1039723362469;
        IEsiClient esiClient;
        public Worker(IEsiClient client, ILogger<Worker> logger)
        {
            esiClient = client;
            Logger = logger;
        }

        public static HttpListener listener;
        public static string url = "http://localhost:8080/";
        public static int pageViews = 0;
        public static int requestCount = 0;
        public static string pageData =
            "<!DOCTYPE>" +
            "<html>" +
            "  <head>" +
            "    <title>HttpListener Example</title>" +
            "  </head>" +
            "  <body>" +
            "    <p>Page Views: {0}</p>" +
            "    <form method=\"post\" action=\"shutdown\">" +
            "      <input type=\"submit\" value=\"Shutdown\" {1}>" +
            "    </form>" +
            "  </body>" +
            "</html>";


        public static async Task<string> ReceiveSsoCallback()
        {
            bool runServer = true;

            // While a user hasn't visited the `shutdown` url, keep on handling requests
            while (runServer)
            {
                // Will wait here until we hear from a connection
                HttpListenerContext ctx = await listener.GetContextAsync();

                // If `shutdown` url requested w/ POST, then shutdown the server after serving the page
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/callback")
                {
                    var pairs = HttpUtility.ParseQueryString(ctx.Request.Url.Query);
                    string output = string.Empty;
                    if(pairs != null) output = pairs["code"];
                    string responseString = "<HTML><BODY> Congratulations! You've logged in! Heydieles market bot liked that.!</BODY></HTML>";
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                    // Get a response stream and write the response to it.
                    ctx.Response.ContentLength64 = buffer.Length;
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    // You must close the output stream.
                    ctx.Response.OutputStream.Close();
                    return output;
                }
            }
            return string.Empty;
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

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            listener = new HttpListener();
            listener.Prefixes.Add(url);
            listener.Start();
            string state = "blah";
            Process.Start(new ProcessStartInfo
            {
                FileName = esiClient.SSO.CreateAuthenticationUrl(new List<string>() { "publicData", "esi-markets.structure_markets.v1" }, state),
                UseShellExecute = true
            });
            string ssoResponse = await ReceiveSsoCallback();
            Console.WriteLine(ssoResponse);
            listener.Stop();
            listener.Close();

            SsoToken token = await esiClient.SSO.GetToken(GrantType.AuthorizationCode, ssoResponse);
            AuthorizedCharacterData characterData = await esiClient.SSO.Verify(token);
            esiClient.SetCharacterData(characterData);

            SQLiteConnection conn = new SQLiteConnection(new SQLiteConnectionStringBuilder() { DataSource = dbPath, ReadOnly = true }.ToString());

            conn.Open();
            Dictionary<int, StockedItem> requestedItems = new Dictionary<int, StockedItem>();

            StructuredRequest strucReq = new StructuredRequest()
            {
                market_name = "jita",
                persist = false
            };
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = itemsQuery;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int typeId = reader.GetInt32(0);
                    string name = reader.GetString(1);
                    requestedItems.Add(typeId, new StockedItem() { TypeId = typeId, Name = name, MinimumPrice = decimal.MaxValue, StockCount = 0 });
                    strucReq.items.Add(new ItemRequest()
                    {
                        type_id = typeId
                    });
                }
            }

            using (HttpClient client = new())
            {
                client.BaseAddress = new Uri("https://evepraisal.com");
                var request = new HttpRequestMessage(HttpMethod.Post, "/appraisal/structured.json");
                PopulateUserAgent(request.Headers);

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));

                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                request.Content = JsonContent.Create(strucReq); //new StringContent("{\"market_name\": \"jita\", \"items\": [{\"name\": \"Rifter\"}, {\"type_id\": 34}], \"persist\":\"no\"}");
                var evepraisalResponse = await client.SendAsync(request);
                if (evepraisalResponse.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    using (var stream = evepraisalResponse.Content.ReadAsStream())
                    using (var gzipStream = new GZipStream(stream, CompressionMode.Decompress))
                    {
                        StructuredResponse? strucResp = JsonSerializer.Deserialize<StructuredResponse>(gzipStream);
                        if (strucResp != null)
                        {
                            foreach(var strucItem in strucResp.appraisal.items)
                            {
                                if(requestedItems.TryGetValue(strucItem.typeID, out var stockedItem))
                                {
                                    stockedItem.JitaPrice = strucItem.prices.sell.min;
                                }
                            }
                        }
                    }
                }
            }

            //foreach (var stockedItem in new List<StockedItem>(requestedItems.Values))
            //{
            //    EsiResponse<List<Statistic>> typeStats = new EsiResponse<List<Statistic>>(new HttpResponseMessage() { StatusCode = HttpStatusCode.BadRequest }, "");
            //    while (typeStats.StatusCode != HttpStatusCode.OK)
            //    {
            //        typeStats = await esiClient.Market.TypeHistoryInRegion(essenceRegionId, stockedItem.TypeId);
            //        if (typeStats.StatusCode == System.Net.HttpStatusCode.OK)
            //        {
            //            break;
            //        }
            //        Console.WriteLine("Waiting and repeating request...");
            //        Thread.Sleep(500);
            //    }
            //    Thread.Sleep(250); // be nice to the server
            //    if (typeStats.Data.Count > 7)
            //    {
            //        long totalVolume = 0;
            //        foreach (var typeStat in typeStats.Data)
            //        {
            //            if ((DateTime.Now - typeStat.Date).TotalDays < 7)
            //            {
            //                totalVolume += typeStat.Volume;
            //            }
            //        }
            //        totalVolume /= 7;
            //        if (totalVolume < 5) // at least 5 per day
            //        {
            //            requestedItems.Remove(stockedItem.TypeId);
            //        }
            //        else
            //        {
            //            stockedItem.VolumePerDay = (int)totalVolume;
            //        }
            //    }
            //    Thread.Sleep(250); // be nice to the server
            //}

            var structOrdersResp = await esiClient.Market.StructureOrders(heydielesHqId);
            while (structOrdersResp.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine("Waiting and repeating request...");
                Thread.Sleep(500);
                structOrdersResp = await esiClient.Market.StructureOrders(heydielesHqId);
            }
            ProcessStructureOrders(requestedItems, structOrdersResp);
            Thread.Sleep(250); // be nice to the server
            if (structOrdersResp.Pages != null)
            {
                int numPages = (int)structOrdersResp.Pages;
                for (int pageId = 2; pageId <= numPages; pageId++)
                {
                    structOrdersResp = await esiClient.Market.StructureOrders(heydielesHqId, pageId);
                    while (structOrdersResp.StatusCode != HttpStatusCode.OK)
                    {
                        Console.WriteLine("Waiting and repeating request...");
                        Thread.Sleep(500);
                        structOrdersResp = await esiClient.Market.StructureOrders(heydielesHqId, pageId);
                    }
                    ProcessStructureOrders(requestedItems, structOrdersResp);
                    Thread.Sleep(250); // be nice to the server
                }
            }
            conn.Close();

            //if(response.StatusCode == System.Net.HttpStatusCode.OK)
            //{
            //    orders.AddRange(response.Data);
            //    if(response.Pages != null)
            //    {
            //        int numPages = response.Pages.Value;
            //        for(int page = 2; page <= numPages; page++)
            //        {
            //            for(int retry = 1; retry < 3; retry++)
            //            {
            //                response = await _client.Market.StructureOrders(heydielesHqId, page);
            //                if(response.StatusCode == System.Net.HttpStatusCode.OK)
            //                {
            //                    break;
            //                }
            //                Thread.Sleep(500);
            //            }
            //            if(response.StatusCode != System.Net.HttpStatusCode.OK)
            //            {
            //                Logger.LogError("Request failed on page " + page);
            //                return;
            //            }
            //        }
            //    }
            //}
        }

        private static void ProcessStructureOrders(Dictionary<int, StockedItem> pricePerItem, EsiResponse<List<Order>> structOrdersResp)
        {
            foreach (var structOrder in structOrdersResp.Data)
            {
                if (!structOrder.IsBuyOrder)
                {
                    if (pricePerItem.TryGetValue(structOrder.TypeId, out StockedItem? stockedItem) && stockedItem != null)
                    {
                        if (structOrder.Price < stockedItem.MinimumPrice)
                        {
                            stockedItem.MinimumPrice = structOrder.Price;
                        }
                        stockedItem.StockCount += structOrder.VolumeRemain;
                    }
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}