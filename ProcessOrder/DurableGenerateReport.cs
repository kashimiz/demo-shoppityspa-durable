using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using ProcessOrder.Models;
using Stripe;
using System.Net;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Newtonsoft.Json;
using System;

namespace ProcessOrder
{
    public static class DurableGenerateReport
    {
        [FunctionName("DurableGenerateReport")]
        public static async Task<dynamic> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var tasks = new List<Task<OrderDetails>>();
            var orderTotals = new List<double>();

            // Get all transactions from Cosmos
            var transactions = await context.CallActivityAsync<IEnumerable<StripeCharge>>("Durable_GetTransactions", 100);
            
            foreach(var transaction in transactions)
            {
                // Create a task to lookup in the information on the order
                tasks.Add(context.CallActivityAsync<OrderDetails>("Durable_GetOrderProcess", transaction));
            }

            // Execution all those tasks IN PARALLEL
            await Task.WhenAll(tasks);

            // Summarize the results
            return from details in tasks
                   group details by details.Result.status into s
                   select new { status = s.Key.ToString(), count = s.Count() };
                   
        }

        [FunctionName("Durable_GetTransactions")]
        public static IEnumerable<StripeCharge> GetTransactions(
            [ActivityTrigger] double maxRecords, 
            TraceWriter log)
        {
            log.Info($"Fetching documents from CosmosDb");
            var docs = client.CreateDocumentQuery<StripeCharge>(UriFactory.CreateDocumentCollectionUri("store", "orders"), "SELECT top 100  * FROM c");
            return docs;
        }

        [FunctionName("DurableGenerateReport_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            TraceWriter log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("DurableGenerateReport", null);

            log.Info($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("Durable_GetOrderProcess")]
        public static OrderDetails GetOrderProcess(
            [ActivityTrigger] StripeCharge transaction,
            TraceWriter log)
        {
            log.Info($"Getting order details for order id: ${transaction.Id}");
            return new OrderDetails { status = (Status)(transaction.Created.Minute % 4 + 1), orderId = transaction.Id };
        }

        private static string EndpointUrl = Environment.GetEnvironmentVariable("EndpointUrl");
        private static string PrimaryKey = Environment.GetEnvironmentVariable("PrimaryKey");
        private static DocumentClient client = new DocumentClient(new Uri(EndpointUrl), PrimaryKey);
    }
}