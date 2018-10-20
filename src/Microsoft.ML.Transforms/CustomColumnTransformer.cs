// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Core.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Model;
using System;
using System.Linq;

namespace Microsoft.ML.Transforms
{
    public sealed class CustomColumnTransformer<TInput, TOutput> : OneToOneTransformerBase
    {
        internal const string LoaderSignature = "CustomTransformer";

        private readonly TransformAction _transformAction;

        public ColumnType ColumnType { get; }
        public string InputColumn => ColumnPairs[0].input;
        public string OutputColumn => ColumnPairs[0].output;

        private readonly string _signature;

        public delegate void TransformAction(in TInput input, ref TOutput output);

        public CustomColumnTransformer(IHostEnvironment env, string inputColumn, string outputColumn, TransformAction transform, string signature, ColumnType columnType)
            : base(Contracts.CheckRef(env, nameof(env)).Register(nameof(CustomColumnTransformer<TInput, TOutput>)), new[] { (inputColumn, outputColumn) })
        {
            Host.CheckValue(transform, nameof(transform));
            Host.CheckNonEmpty(signature, nameof(signature));
            Host.CheckValue(columnType, nameof(columnType));

            Host.CheckParam(columnType.RawType == typeof(TOutput), nameof(columnType));

            _transformAction = transform;
            _signature = signature;

            ColumnType = columnType;
        }

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "CUSTOMXF",
                verWrittenCur: 0x00010001, // Initial
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(CustomColumnTransformer<TInput, TOutput>).Assembly.FullName);
        }

        public override void Save(ModelSaveContext ctx)
        {
            ctx.SetVersionInfo(GetVersionInfo());
            // *** Binary format ***
            // <base>
            // string: signature

            SaveColumns(ctx);
            ctx.SaveNonEmptyString(_signature);
        }

        protected override IRowMapper MakeRowMapper(ISchema schema) => new Mapper(this, Schema.Create(schema));

        private sealed class Mapper : MapperBase
        {
            private readonly CustomColumnTransformer<TInput, TOutput> _parent;

            public Mapper(CustomColumnTransformer<TInput, TOutput> parent, Schema inputSchema)
                : base(parent.Host.Register(nameof(Mapper)), parent, inputSchema)
            {
                _parent = parent;
            }

            public override Schema.Column[] GetOutputColumns()
                => _parent.ColumnPairs.Select(x => new Schema.Column(x.output, _parent.ColumnType, null)).ToArray();

            protected override Delegate MakeGetter(IRow input, int iinfo, out Action disposer)
            {
                Host.Assert(iinfo == 0);
                input.Schema.TryGetColumnIndex(_parent.ColumnPairs[iinfo].input, out int srcCol);

                var srcGetter = input.GetGetter<TInput>(srcCol);
                TInput srcValue = default;
                ValueGetter<TOutput> getter = (ref TOutput dst) =>
                {
                    srcGetter(ref srcValue);
                    _parent._transformAction(in srcValue, ref dst);
                };
                disposer = null;
                return getter;
            }
        }
    }

    public sealed class CustomColumnEstimator<TInput, TOutput> : TrivialEstimator<CustomColumnTransformer<TInput, TOutput>>
    {
        public CustomColumnEstimator(IHostEnvironment env, string inputColumn, string outputColumn, CustomColumnTransformer<TInput, TOutput>.TransformAction transform, string signature, ColumnType columnType)
            : base(Contracts.CheckRef(env, nameof(env)).Register(nameof(CustomColumnEstimator<TInput, TOutput>)),
                  new CustomColumnTransformer<TInput, TOutput>(env, inputColumn, outputColumn, transform, signature, columnType))
        {
        }

        public override SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            Host.CheckValue(inputSchema, nameof(inputSchema));
            var result = inputSchema.Columns.ToDictionary(x => x.Name);
            if (!inputSchema.TryFindColumn(Transformer.InputColumn, out var col))
                throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", Transformer.InputColumn);
            if (!col.ItemType.IsText || col.Kind != SchemaShape.Column.VectorKind.Scalar)
                throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", Transformer.InputColumn, TextType.Instance.ToString(), col.GetTypeString());

            result[Transformer.OutputColumn] = new SchemaShape.Column(Transformer.OutputColumn, SchemaShape.Column.VectorKind.Scalar, Transformer.ColumnType, false);

            return new SchemaShape(result.Values);
        }
    }
}
