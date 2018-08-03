﻿using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Learners;
using Microsoft.ML.Runtime.Api;
using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Microsoft.ML.Models;

namespace Microsoft.ML.Tests.Scenarios.Api
{
    public partial class ApiScenariosTests
    {
        /// <summary>
        /// Evaluation: Similar to the simple train scenario, except instead of having some 
        /// predictive structure, be able to score another "test" data file, run the result 
        /// through an evaluator and get metrics like AUC, accuracy, PR curves, and whatnot. 
        /// Getting metrics out of this shoudl be as straightforward and unannoying as possible.
        /// </summary>
        [Fact]
        public void Evaluation()
        {
            var dataPath = GetDataPath(SentimentDataPath);
            var testDataPath = GetDataPath(SentimentTestPath);

            using (var env = new TlcEnvironment(seed: 1, conc: 1))
            {
                // Pipeline
                var loader = new TextLoader(env, MakeSentimentTextLoaderArgs(), new MultiFileSource(dataPath));

                var trans = TextTransform.Create(env, MakeSentimentTextTransformArgs(), loader);

                // Train
                var trainer = new LinearClassificationTrainer(env, new LinearClassificationTrainer.Arguments
                {
                    NumThreads = 1
                });

                var cached = new CacheDataView(env, trans, prefetch: null);
                var trainRoles = new RoleMappedData(cached, label: "Label", feature: "Features");
                var predictor = trainer.Train(new Runtime.TrainContext(trainRoles));
                var scoreRoles = new RoleMappedData(trans, label: "Label", feature: "Features");
                IDataScorerTransform scorer = ScoreUtils.GetScorer(predictor, scoreRoles, env, trainRoles.Schema);

                var dataEval = new RoleMappedData(scorer, label: "Label", feature: "Features", opt: true);

                var evaluator = new BinaryClassifierMamlEvaluator(env, new BinaryClassifierMamlEvaluator.Arguments() { });
                var metricsDict = evaluator.Evaluate(dataEval);

                var metrics = BinaryClassificationMetrics.FromMetrics(env, metricsDict["OverallMetrics"], metricsDict["ConfusionMatrix"])[0];
            }
        }
    }
}
