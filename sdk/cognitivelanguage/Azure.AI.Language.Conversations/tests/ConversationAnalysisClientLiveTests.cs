// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.AI.Language.Conversations.Models;
using NUnit.Framework;

namespace Azure.AI.Language.Conversations.Tests
{
    public class ConversationAnalysisClientLiveTests : ConversationAnalysisTestBase<ConversationAnalysisClient>
    {
        public ConversationAnalysisClientLiveTests(bool isAsync, ConversationAnalysisClientOptions.ServiceVersion serviceVersion)
            : base(isAsync, serviceVersion, null /* RecordedTestMode.Record /* to record */)
        {
        }
        private static string EnglishText = "We'll have 2 plates of seared salmon nigiri.";
        private static string SpanishText = "Tendremos 2 platos de nigiri de salmón braseado.";

        [Test]
        public async Task AnalyzeConversation()
        {
            ConversationAnalysisClient client = Client;

            AnalyzeConversationOptions options = new AnalyzeConversationOptions(
               TestEnvironment.ProjectName,
               TestEnvironment.DeploymentName,
               EnglishText);

            Response<AnalyzeConversationResult> response = await client.AnalyzeConversationAsync(options);

            Assert.That(response.Value.Prediction.TopIntent, Is.EqualTo("Order"));
        }

        [Test]
        public async Task AnalyzeConversationWithLanguage()
        {
            ConversationAnalysisClient client = Client;

            AnalyzeConversationOptions options = new AnalyzeConversationOptions(
               TestEnvironment.ProjectName,
               TestEnvironment.DeploymentName,
               SpanishText)
            {
                Language = "es"
            };

            Response<AnalyzeConversationResult> response = await client.AnalyzeConversationAsync(options);

            Assert.That(response.Value.Prediction.TopIntent, Is.EqualTo("Order"));
        }

        [Test]
        public async Task AnalyzeConversationsDeepstack()
        {
            ConversationAnalysisClient client = Client;

            AnalyzeConversationOptions options = new AnalyzeConversationOptions(
               TestEnvironment.ProjectName,
               TestEnvironment.DeploymentName,
               EnglishText);

            Response<AnalyzeConversationResult> response = await client.AnalyzeConversationAsync(options);

            DeepstackPrediction deepstackPrediction = response.Value.Prediction as DeepstackPrediction;

            Assert.That(deepstackPrediction.Entities, Is.Not.Null);
            Assert.That(deepstackPrediction.TopIntent, Is.EqualTo("Order"));
        }
    }
}
