﻿using Microsoft.ML.Core.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Api;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Data.IO;
using Microsoft.ML.Runtime.Learners;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.Tests.Scenarios.Api;
using System;
using System.Collections.Generic;
using System.IO;
[assembly: LoadableClass(typeof(TransformWrapper), null, typeof(SignatureLoadModel),
    "Transform wrapper", TransformWrapper.LoaderSignature)]
[assembly: LoadableClass(typeof(LoaderWrapper), null, typeof(SignatureLoadModel),
    "Loader wrapper", LoaderWrapper.LoaderSignature)]

namespace Microsoft.ML.Tests.Scenarios.Api
{
    public sealed class LoaderWrapper : IDataReader<IMultiStreamSource>, ICanSaveModel
    {
        public const string LoaderSignature = "LoaderWrapper";

        private readonly IHostEnvironment _env;
        private readonly Func<IMultiStreamSource, IDataLoader> _loaderFactory;

        public LoaderWrapper(IHostEnvironment env, Func<IMultiStreamSource, IDataLoader> loaderFactory)
        {
            _env = env;
            _loaderFactory = loaderFactory;
        }

        public ISchema GetOutputSchema()
        {
            var emptyData = Read(new MultiFileSource(null));
            return emptyData.Schema;
        }

        public IDataView Read(IMultiStreamSource input) => _loaderFactory(input);

        public void Save(ModelSaveContext ctx)
        {
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());
            var ldr = Read(new MultiFileSource(null));
            ctx.SaveModel(ldr, "Loader");
        }

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "LDR WRPR",
                verWrittenCur: 0x00010001, // Initial
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature);
        }

        public LoaderWrapper(IHostEnvironment env, ModelLoadContext ctx)
        {
            ctx.CheckAtModel(GetVersionInfo());
            ctx.LoadModel<IDataLoader, SignatureLoadDataLoader>(env, out var loader, "Loader", new MultiFileSource(null));

            var loaderStream = new MemoryStream();
            using (var rep = RepositoryWriter.CreateNew(loaderStream))
            {
                ModelSaveContext.SaveModel(rep, loader, "Loader");
                rep.Commit();
            }

            _env = env;
            _loaderFactory = (IMultiStreamSource source) =>
            {
                using (var rep = RepositoryReader.Open(loaderStream))
                {
                    ModelLoadContext.LoadModel<IDataLoader, SignatureLoadDataLoader>(env, out var ldr, rep, "Loader", source);
                    return ldr;
                }
            };

        }
    }

    public class TransformWrapper : ITransformer, ICanSaveModel
    {
        public const string LoaderSignature = "TransformWrapper";
        private const string TransformDirTemplate = "Step_{0:000}";

        private readonly IHostEnvironment _env;
        private readonly IDataView _xf;

        public TransformWrapper(IHostEnvironment env, IDataView xf)
        {
            _env = env;
            _xf = xf;
        }

        public ISchema GetOutputSchema(ISchema inputSchema)
        {
            var dv = new EmptyDataView(_env, inputSchema);
            var output = ApplyTransformUtils.ApplyAllTransformsToData(_env, _xf, dv);
            return output.Schema;
        }

        public void Save(ModelSaveContext ctx)
        {
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            var dataPipe = _xf;
            var transforms = new List<IDataTransform>();
            while (dataPipe is IDataTransform xf)
            {
                // REVIEW: a malicious user could construct a loop in the Source chain, that would
                // cause this method to iterate forever (and throw something when the list overflows). There's
                // no way to insulate from ALL malicious behavior.
                transforms.Add(xf);
                dataPipe = xf.Source;
                Contracts.AssertValue(dataPipe);
            }
            transforms.Reverse();

            ctx.SaveSubModel("Loader", c => BinaryLoader.SaveInstance(_env, c, dataPipe.Schema));

            ctx.Writer.Write(transforms.Count);
            for (int i = 0; i < transforms.Count; i++)
            {
                var dirName = string.Format(TransformDirTemplate, i);
                ctx.SaveModel(transforms[i], dirName);
            }
        }

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "XF  WRPR",
                verWrittenCur: 0x00010001, // Initial
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature);
        }

        public TransformWrapper(IHostEnvironment env, ModelLoadContext ctx)
        {
            ctx.CheckAtModel(GetVersionInfo());
            int n = ctx.Reader.ReadInt32();

            ctx.LoadModel<IDataLoader, SignatureLoadDataLoader>(env, out var loader, "Loader", new MultiFileSource(null));

            IDataView data = loader;
            for (int i = 0; i < n; i++)
            {
                var dirName = string.Format(TransformDirTemplate, i);
                ctx.LoadModel<IDataTransform, SignatureLoadDataTransform>(env, out var xf, dirName, data);
                data = xf;
            }

            _env = env;
            _xf = data;
        }

        public IDataView Transform(IDataView input) => ApplyTransformUtils.ApplyAllTransformsToData(_env, _xf, input);
    }


    public class MyTextLoader : IDataReaderEstimator<IMultiStreamSource, LoaderWrapper>
    {
        private readonly TextLoader.Arguments _args;
        private readonly IHostEnvironment _env;

        public MyTextLoader(IHostEnvironment env, TextLoader.Arguments args)
        {
            _env = env;
            _args = args;
        }

        public LoaderWrapper Fit(IMultiStreamSource input)
        {
            return new LoaderWrapper(_env, x => new TextLoader(_env, _args, x));
        }

        public SchemaShape GetOutputSchema()
        {
            var emptyData = new TextLoader(_env, _args, new MultiFileSource(null));
            return SchemaShape.Create(emptyData.Schema);
        }
    }

    public abstract class TrainerBase : IEstimator<TransformWrapper>
    {
        protected readonly IHostEnvironment _env;
        private readonly string _featureCol;
        private readonly string _labelCol;
        private readonly bool _cache;

        protected TrainerBase(IHostEnvironment env, bool cache, string featureColumn, string labelColumn)
        {
            _env = env;
            _cache = cache;
            _featureCol = featureColumn;
            _labelCol = labelColumn;
        }

        public TransformWrapper Fit(IDataView input)
        {
            var cached = _cache ? new CacheDataView(_env, input, prefetch: null) : input;

            var trainRoles = new RoleMappedData(cached, label: _labelCol, feature: _featureCol);
            var pred = Train(trainRoles);

            var emptyData = new EmptyDataView(_env, input.Schema);
            var scoreRoles = new RoleMappedData(emptyData, label: _labelCol, feature: _featureCol);
            IDataScorerTransform scorer = ScoreUtils.GetScorer(pred, scoreRoles, _env, trainRoles.Schema);
            return new TransformWrapper(_env, scorer);
        }

        public SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            throw new NotImplementedException();
        }

        protected abstract IPredictor Train(RoleMappedData data);
    }

    public class MyTextTransform : IEstimator<TransformWrapper>
    {
        private readonly IHostEnvironment _env;
        private readonly TextTransform.Arguments _args;

        public MyTextTransform(IHostEnvironment env, TextTransform.Arguments args)
        {
            _env = env;
            _args = args;
        }

        public TransformWrapper Fit(IDataView input)
        {
            var xf = TextTransform.Create(_env, _args, input);
            var empty = new EmptyDataView(_env, input.Schema);
            var chunk = ApplyTransformUtils.ApplyAllTransformsToData(_env, xf, empty, input);
            return new TransformWrapper(_env, chunk);
        }

        public SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            throw new NotImplementedException();
        }
    }

    public sealed class MySdca : TrainerBase
    {
        private readonly LinearClassificationTrainer.Arguments _args;

        public MySdca(IHostEnvironment env, LinearClassificationTrainer.Arguments args, string featureCol, string labelCol)
            : base(env, true, featureCol, labelCol)
        {
            _args = args;
        }

        protected override IPredictor Train(RoleMappedData data) => new LinearClassificationTrainer(_env, _args).Train(data);
    }

    public sealed class MyPredictionEngine<TSrc, TDst>
                where TSrc : class
                where TDst : class, new()
    {
        private readonly PredictionEngine<TSrc, TDst> _engine;

        public MyPredictionEngine(IHostEnvironment env, ISchema inputSchema, ITransformer pipe)
        {
            IDataView dv = new EmptyDataView(env, inputSchema);
            _engine = env.CreatePredictionEngine<TSrc, TDst>(pipe.Transform(dv));
        }

        public TDst Predict(TSrc example)
        {
            return _engine.Predict(example);
        }
    }


}
