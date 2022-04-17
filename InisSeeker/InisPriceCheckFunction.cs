using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using HtmlAgilityPack;
using InisSeeker.Properties;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using TableEntity = Azure.Data.Tables.TableEntity;

namespace InisSeeker
{
    public class InisPriceCheckFunction
    {
        [FunctionName("InisSeeker1")]
        public async Task Run([TimerTrigger("0 */5 * * * *")]TimerInfo myTimer, ILogger log)
        {
            var lowestPriceCompanyName = string.Empty;
            var lowestPriceWebpageLink = string.Empty;
            var currentLowestPrice = new decimal();
            var lastStoredPrice = new decimal?();
            var webClient = new HttpClient();
            var htmlContent = await webClient.GetStringAsync(Resources.InisUrl);

            //Get lowest price
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);
            var nodes = doc.DocumentNode.SelectNodes("//span[contains(@class, 'MuiTypography-root')]").ToList();
            if (nodes is not null)
            {
                foreach (var node in nodes)
                {
                    Console.WriteLine(node.InnerText);
                    if (!node.InnerText.Equals("Inis")) continue;
                    if (nodes[nodes.IndexOf(node) + 2].InnerText.ToUpper() != "LOWEST PRICE") continue;
                    if (!decimal.TryParse(nodes[nodes.IndexOf(node) + 4].InnerText.Trim('$'), out currentLowestPrice)) continue;
                    lowestPriceCompanyName = nodes[nodes.IndexOf(node) + 3].InnerText;
                    break;
                }

                Console.WriteLine(currentLowestPrice + "\n\n\n");
            }

            //Clear everything before the next step
            nodes.Clear();
            doc = new HtmlDocument();

            //Get link to lowest price website
            htmlContent = await webClient.GetStringAsync(Resources.InisPricingPage);
            doc.LoadHtml(htmlContent);
            var nodesArray = doc.DocumentNode.SelectNodes("//a[contains(@class, 'MuiTypography-root')]");
            if (nodesArray is not null)
            {
                foreach (var node in nodesArray)
                {
                    if (node.InnerText != lowestPriceCompanyName) continue;
                    lowestPriceWebpageLink = node.ParentNode.ParentNode.ParentNode.ParentNode.ChildNodes[^1].FirstChild
                        .GetAttributeValue("href", null);
                    break;
                }
            }

            //Check table for last price
            var inisPriceTable = new TableClient(Resources.StorageServiceConnectionString, "LatestPrice");
            var queryResult = inisPriceTable.Query<TableEntity>(filter: $"PartitionKey eq 'Price'");
            foreach (var result in queryResult)
            {
                lastStoredPrice = (decimal?)result.GetDouble("Price");
            }

            var emailMessage = string.Empty;
            if (lastStoredPrice == currentLowestPrice)
            {
                log.LogInformation($"The price is still at {lastStoredPrice}");
                return;
            }
            if (lastStoredPrice < currentLowestPrice)
            {
                log.LogInformation($"Price went up from {lastStoredPrice} to {currentLowestPrice}");
                emailMessage =
                    $"Inis has gone up in price from {(decimal) lastStoredPrice} to {currentLowestPrice}.\nLooks like you missed out. It's here if you want to grab it: {lowestPriceWebpageLink}";
            }

            if (lastStoredPrice > currentLowestPrice)
            {
                log.LogInformation($"Price went down from {lastStoredPrice} to {currentLowestPrice}");
                emailMessage = $"Inis has gone down in price from {(decimal) lastStoredPrice} to {currentLowestPrice}.\nIt's here if you want to grab it: {lowestPriceWebpageLink}";
            }

            var tableService = new TableEntity("Price", "1")
            {
                {"Price", currentLowestPrice}
            };
            await inisPriceTable.UpdateEntityAsync(tableService, ETag.All);

            //Send mail to people who care
            using SmtpClient client = new("smtp.gmail.com", 587)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(Properties.Resources.GmailUserName, Properties.Resources.GmailPassword)
            };

            var msg = new MailMessage()
            {
                To = { "matt.benedict1701@gmail.com", "jordan.breakstone@colorado.edu" },
                From = new MailAddress("mitchtheautomationbot@gmail.com"),
                Subject = "Inis Price Drop Alert",
                Body = emailMessage
            };
            client.Send(msg);

            log.LogInformation("Price drop");
        }
    }
}
