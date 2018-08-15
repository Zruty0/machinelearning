﻿using Microsoft.ML.Models;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Learners;
using System.Collections;
using Xunit;

namespace Microsoft.ML.Tests.Scenarios.Api
{
    public partial class ApiScenariosTests
    {
        /// <summary>
        /// Reconfigurable predictions: The following should be possible: A user trains a binary classifier,
        /// and through the test evaluator gets a PR curve, the based on the PR curve picks a new threshold
        /// and configures the scorer (or more precisely instantiates a new scorer over the same predictor)
        /// with some threshold derived from that.
        /// </summary>
        [Fact]
        void ReconfigurablePrediction()
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

                var bindable = ScoreUtils.GetSchemaBindableMapper(env, predictor, null);
                var mapper = bindable.Bind(env, trainRoles.Schema);
                var newScorer = new BinaryClassifierScorer(env, new BinaryClassifierScorer.Arguments { Threshold = 0.01f, ThresholdColumn = DefaultColumnNames.Probability },
                    scoreRoles.Data, mapper, trainRoles.Schema);

                dataEval = new RoleMappedData(newScorer, label: "Label", feature: "Features", opt: true);
                var new_evaluator = new BinaryClassifierMamlEvaluator(env, new BinaryClassifierMamlEvaluator.Arguments() { Threshold = 0.01f, UseRawScoreThreshold = false });
                metricsDict = new_evaluator.Evaluate(dataEval);
                var new_metrics = BinaryClassificationMetrics.FromMetrics(env, metricsDict["OverallMetrics"], metricsDict["ConfusionMatrix"])[0];
            }
        }

        /// <summary>
        /// Reconfigurable predictions: The following should be possible: A user trains a binary classifier,
        /// and through the test evaluator gets a PR curve, the based on the PR curve picks a new threshold
        /// and configures the scorer (or more precisely instantiates a new scorer over the same predictor)
        /// with some threshold derived from that.
        /// </summary>
        [Fact]
        void New_ReconfigurablePrediction()
        {
            var dataPath = GetDataPath(SentimentDataPath);
            var testDataPath = GetDataPath(SentimentTestPath);

            using (var env = new TlcEnvironment(seed: 1, conc: 1))
            {
                var dataReader = new MyTextLoader(env, MakeSentimentTextLoaderArgs())
                    .Fit(new MultiFileSource(dataPath));

                var data = dataReader.Read(new MultiFileSource(dataPath));
                var testData = dataReader.Read(new MultiFileSource(testDataPath));

                // Pipeline.
                var pipeline = new MyTextTransform(env, MakeSentimentTextTransformArgs())
                    .Fit(data);

                var trainer = new MySdca(env, new LinearClassificationTrainer.Arguments { NumThreads = 1 }, "Features", "Label");
                var trainData = pipeline.Transform(data);
                var model = trainer.Fit(trainData);

                var scoredTest = model.Transform(pipeline.Transform(testData));
                var metrics = new MyBinaryClassifierEvaluator(env, new BinaryClassifierEvaluator.Arguments()).Evaluate(scoredTest, "Label", "Probability");

                var newModel = model.Clone(new BinaryClassifierScorer.Arguments { Threshold = 0.01f, ThresholdColumn = DefaultColumnNames.Probability });
                var newScoredTest = newModel.Transform(pipeline.Transform(testData));
                var newMetrics = new MyBinaryClassifierEvaluator(env, new BinaryClassifierEvaluator.Arguments { Threshold = 0.01f, UseRawScoreThreshold = false }).Evaluate(newScoredTest, "Label", "Probability");
            }

        }

    }
}
