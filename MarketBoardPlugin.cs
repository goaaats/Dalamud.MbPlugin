using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.Internal;
using Dalamud.Plugin;
using Newtonsoft.Json.Linq;
using Serilog;

namespace DalamudPlugin
{
	public class MarketBoardPlugin : IDalamudPlugin
    {
        public string Name => "Market Board plugin";

        private DalamudPluginInterface pluginInterface;

		#region IDalamudPlugin initialization/deinitialization

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;

            // Set up command handlers
            pluginInterface.CommandManager.AddHandler("/mb", new CommandInfo(OnMarketBoardSearch) {
                HelpMessage = "Query market board information for an item by name or link. Usage: /mb <name of item> [hq] +[world] [- for cheapest on DC] OR /mb <item link> [world] [- for cheapest on DC]" 
            });
        }

        public void Dispose()
        {
            // Remove command handlers
            pluginInterface.CommandManager.RemoveHandler("/mb");
        }

        #endregion

        #region Chat command handlers

        private void OnMarketBoardSearch(string command, string arguments) {
            if (string.IsNullOrEmpty(arguments)) {
                this.pluginInterface.Framework.Gui.Chat.PrintError("No item specified.");
                return;
            }

            this.pluginInterface.Framework.Gui.Chat.Print("Searching for market board data...");
            Task.Run(() => {
                var world = pluginInterface.ClientState.LocalPlayer.CurrentWorld.Name;
                var cheapest = false;

                if (this.pluginInterface.Framework.Gui.Chat.LastLinkedItemId != 0 && arguments.Contains("<item>")) {
                    if (arguments != "<item>")
                        world = arguments.Replace("<item>", "").Replace(" ", "");

                    if (arguments.EndsWith("-")) {
                        cheapest = true;
                    }

                    Task.Run(() => SendItemInfo(this.pluginInterface.Framework.Gui.Chat.LastLinkedItemId, (this.pluginInterface.Framework.Gui.Chat.LastLinkedItemFlags & 1) == 1, world, cheapest));

                    return;
                }

                var isHq = false;
                var parts = arguments.Split();

                if (parts.Contains("hq")) {
                    isHq = true;
                    parts = parts.Where(x => x != "hq").ToArray();
                }

                if (parts[parts.Length - 1].Contains("+")) {
                    world = parts[parts.Length - 1].Replace("+", "");
                    parts = parts.Take(parts.Length - 1).ToArray();
                }

                if (parts[parts.Length - 1] == "-") {
                    cheapest = true;
                    parts = parts.Take(parts.Length - 1).ToArray();
                }

                var searchTerm = string.Join(" ", parts);

                dynamic candidates = Search(searchTerm, "Item").GetAwaiter().GetResult();

                if (candidates.Results.Count == 0) {
                    this.pluginInterface.Framework.Gui.Chat.Print("No items found using that name.");
                    return;
                }

                SendItemInfo((int) candidates.Results[0].ID, isHq, world, cheapest, (string) candidates.Results[0].Name);
            });
        }

        #endregion

        private void SendItemInfo(int itemId, bool hq, string world, bool cheapest = false, string fancyItemName = "") {
            try {
                List<JToken> history = null;
                List<JToken> prices = null;

                if (cheapest) {
                    dynamic worldCandidates = Search(world, "World").GetAwaiter().GetResult();
                    dynamic worldInfo = GetWorld((int) worldCandidates.Results[0].ID).GetAwaiter().GetResult();

                    var mbInfo = GetMarketInfoDc(itemId, (string) worldInfo.DataCenter.Name).GetAwaiter().GetResult();

                    var lowestWorldName = mbInfo.First.Path;
                    var lowestEntry = mbInfo.First.First;
                    foreach (var worldEntry in mbInfo) {

                        var thisPrice = (int) worldEntry.Value["Prices"][0]["PricePerUnit"];
                        var lowestPrice = (int) lowestEntry["Prices"][0]["PricePerUnit"];

                        if (thisPrice < lowestPrice) {
                            lowestEntry = worldEntry.Value;
                            lowestWorldName = worldEntry.Key;
                        }
                    }

                    world = lowestWorldName;

                    history = ((JArray) lowestEntry["History"]).ToList();
                    prices = ((JArray) lowestEntry["Prices"]).ToList();
                } else {
                    dynamic mbInfo = GetMarketInfoWorld(itemId, world).GetAwaiter()
                                           .GetResult();

                    history = ((JArray) mbInfo.History).ToList();
                    prices = ((JArray) mbInfo.Prices).ToList();
                }

                if (hq) {
                    history = history.Where(x => (bool) x["IsHQ"]).ToList();

                    prices = prices.Where(x => (bool) x["IsHQ"]).ToList();
                }

                
                this.pluginInterface.Framework.Gui.Chat.Print($"{(cheapest ? "Cheapest result" : "Result")} {(!string.IsNullOrEmpty(fancyItemName) ? $" for \"{fancyItemName}\"{(hq ? "(HQ)" : "")}" : "")} on {world}:");
                this.pluginInterface.Framework.Gui.Chat.Print(history.Count == 0
                                                  ? "No recent sales for this item."
                                                  : $"Last sale:\n    {DateTimeOffset.FromUnixTimeSeconds((long) history[0]["Added"]).ToString("R")}\n    {history[0]["PricePerUnit"]:N0} /u, {history[0]["Quantity"]} units");

                this.pluginInterface.Framework.Gui.Chat.Print(history.Count == 0
                                                  ? "No current offerings for this item."
                                                  : $"Current lowest offering:\n    {DateTimeOffset.FromUnixTimeSeconds((long) prices[0]["Added"]).ToString("R")}\n    {prices[0]["PricePerUnit"]:N0} /u, {prices[0]["Quantity"]} units");
            } catch (Exception e) {
                this.pluginInterface.Framework.Gui.Chat.PrintError("An error occured when getting market board data.");
                Log.Error(e, "Could not get market board data.");
            }
        }

        #region Web Requests

        private const string BaseUrl = "http://xivapi.com/";
            
        public static async Task<JObject> Search(string query, string indexes, int limit = 100) {
            query = System.Net.WebUtility.UrlEncode(query);

            return await Get("search" + $"?string={query}&indexes={indexes}&limit={limit}");
        }

        public static async Task<JObject> GetMarketInfoWorld(int itemId, string worldName) {
            return await Get($"market/{worldName}/item/{itemId}");
        }

        public static async Task<JObject> GetMarketInfoDc(int itemId, string dcName) {
            return await Get($"market/item/{itemId}?dc={dcName}");
        }

        public static async Task<JObject> GetWorld(int world)
        {
            return await Get("World/" + world);;
        }

        public static async Task<dynamic> Get(string endpoint, params string[] parameters)
        {
            var requestParameters = "?";

            foreach (var parameter in parameters) requestParameters += parameter + "&";

            var client = new HttpClient();
            var response = await client.PostAsync(BaseUrl + endpoint, new StringContent(requestParameters));
            var result = await response.Content.ReadAsStringAsync();

            var obj = JObject.Parse(result);
            return obj;
        }

        #endregion
	}
}
