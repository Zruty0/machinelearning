﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Microsoft.ML.Core.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using Microsoft.ML.Runtime.ImageAnalytics;
using Microsoft.ML.Runtime.Internal.Internallearn;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model;

[assembly: LoadableClass(ImageResizerTransform.Summary, typeof(ImageResizerTransform), typeof(ImageResizerTransform.Arguments),
    typeof(SignatureDataTransform), ImageResizerTransform.UserName, "ImageResizerTransform", "ImageResizer")]

[assembly: LoadableClass(ImageResizerTransform.Summary, typeof(ImageResizerTransform), null, typeof(SignatureLoadDataTransform),
    ImageResizerTransform.UserName, ImageResizerTransform.LoaderSignature)]

[assembly: LoadableClass(typeof(ImageResizerTransform), null, typeof(SignatureLoadModel),
    ImageResizerTransform.UserName, ImageResizerTransform.LoaderSignature)]

[assembly: LoadableClass(typeof(IRowMapper), typeof(ImageResizerTransform.Mapper), null, typeof(SignatureLoadRowMapper),
    ImageResizerTransform.UserName, ImageResizerTransform.LoaderSignature)]

namespace Microsoft.ML.Runtime.ImageAnalytics
{
    // REVIEW: Rewrite as LambdaTransform to simplify.
    /// <summary>
    /// Transform which takes one or many columns of <see cref="ImageType"/> and resize them to provided height and width.
    /// </summary>
    public sealed class ImageResizerTransform : ITransformer, ICanSaveModel
    {
        public enum ResizingKind : byte
        {
            [TGUI(Label = "Isotropic with Padding")]
            IsoPad = 0,

            [TGUI(Label = "Isotropic with Cropping")]
            IsoCrop = 1
        }

        public enum Anchor : byte
        {
            Right = 0,
            Left = 1,
            Top = 2,
            Bottom = 3,
            Center = 4
        }

        public sealed class Column : OneToOneColumn
        {
            [Argument(ArgumentType.AtMostOnce, HelpText = "Width of the resized image", ShortName = "width")]
            public int? ImageWidth;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Height of the resized image", ShortName = "height")]
            public int? ImageHeight;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Resizing method", ShortName = "scale")]
            public ResizingKind? Resizing;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Anchor for cropping", ShortName = "anchor")]
            public Anchor? CropAnchor;

            public static Column Parse(string str)
            {
                Contracts.AssertNonEmpty(str);

                var res = new Column();
                if (res.TryParse(str))
                    return res;
                return null;
            }

            public bool TryUnparse(StringBuilder sb)
            {
                Contracts.AssertValue(sb);
                if (ImageWidth != null || ImageHeight != null || Resizing != null || CropAnchor != null)
                    return false;
                return TryUnparseCore(sb);
            }
        }

        public class Arguments : TransformInputBase
        {
            [Argument(ArgumentType.Multiple | ArgumentType.Required, HelpText = "New column definition(s) (optional form: name:src)", ShortName = "col", SortOrder = 1)]
            public Column[] Column;

            [Argument(ArgumentType.Required, HelpText = "Resized width of the image", ShortName = "width")]
            public int ImageWidth;

            [Argument(ArgumentType.Required, HelpText = "Resized height of the image", ShortName = "height")]
            public int ImageHeight;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Resizing method", ShortName = "scale")]
            public ResizingKind Resizing = ResizingKind.IsoCrop;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Anchor for cropping", ShortName = "anchor")]
            public Anchor CropAnchor = Anchor.Center;
        }

        /// <summary>
        /// Information for each column pair.
        /// </summary>
        public sealed class ColumnInfo
        {
            public readonly string Input;
            public readonly string Output;

            public readonly int Width;
            public readonly int Height;
            public readonly ResizingKind Scale;
            public readonly Anchor Anchor;
            public readonly ColumnType Type;

            public ColumnInfo(string input, string output, int width, int height, ResizingKind scale, Anchor anchor)
            {
                Contracts.CheckNonEmpty(input, nameof(input));
                Contracts.CheckNonEmpty(output, nameof(output));
                Contracts.CheckUserArg(width > 0, nameof(Column.ImageWidth));
                Contracts.CheckUserArg(height > 0, nameof(Column.ImageHeight));
                Contracts.CheckUserArg(Enum.IsDefined(typeof(ResizingKind), scale), nameof(Column.Resizing));
                Contracts.CheckUserArg(Enum.IsDefined(typeof(Anchor), anchor), nameof(Column.CropAnchor));

                Input = input;
                Output = output;
                Width = width;
                Height = height;
                Scale = scale;
                Anchor = anchor;
                Type = new ImageType(Height, Width);
            }
        }

        internal const string Summary = "Scales an image to specified dimensions using one of the three scale types: isotropic with padding, "
            + "isotropic with cropping or anisotropic. In case of isotropic padding, transparent color is used to pad resulting image.";

        internal const string UserName = "Image Resizer Transform";
        public const string LoaderSignature = "ImageScalerTransform";
        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "IMGSCALF",
                //verWrittenCur: 0x00010001, // Initial
                verWrittenCur: 0x00010002, // Swith from OpenCV to Bitmap
                verReadableCur: 0x00010002,
                verWeCanReadBack: 0x00010002,
                loaderSignature: LoaderSignature);
        }

        private const string RegistrationName = "ImageScaler";

        private readonly IHost _host;
        private readonly ColumnInfo[] _columns;

        public IReadOnlyCollection<ColumnInfo> Columns => _columns.AsReadOnly();

        public ImageResizerTransform(IHostEnvironment env, string inputColumn, string outputColumn,
            int imageWidth, int imageHeight, ResizingKind resizing = ResizingKind.IsoCrop, Anchor cropAnchor = Anchor.Center)
            : this(env, new ColumnInfo(inputColumn, outputColumn, imageWidth, imageHeight, resizing, cropAnchor))
        {
        }

        public ImageResizerTransform(IHostEnvironment env, params ColumnInfo[] columns)
        {
            Contracts.CheckValue(env, nameof(env));
            _host = env.Register(RegistrationName);
            _host.CheckValue(columns, nameof(columns));

            _columns = columns.ToArray();
        }

        // Public constructor corresponding to SignatureDataTransform.
        public static IDataTransform Create(IHostEnvironment env, Arguments args, IDataView input)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(args, nameof(args));
            env.CheckValue(input, nameof(input));

            env.CheckValue(args.Column, nameof(args.Column));

            var cols = new ColumnInfo[args.Column.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                var item = args.Column[i];
                cols[i] = new ColumnInfo(
                    item.Source ?? item.Name,
                    item.Name,
                    item.ImageWidth ?? args.ImageWidth,
                    item.ImageHeight ?? args.ImageHeight,
                    item.Resizing ?? args.Resizing,
                    item.CropAnchor ?? args.CropAnchor);
            }

            var transformer = new ImageResizerTransform(env, cols);
            return new RowToRowMapperTransform(env, input, transformer.MakeRowMapper(input.Schema));
        }

        public ImageResizerTransform(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            _host = env.Register(RegistrationName);

            _host.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());

            // *** Binary format ***
            // int: sizeof(float)
            // int: number of added columns
            // for each added column
            //   int: id of output column name
            //   int: id of input column name

            // for each added column
            //   int: width
            //   int: height
            //   byte: scaling kind

            int cbFloat = ctx.Reader.ReadInt32();
            _host.CheckDecode(cbFloat == sizeof(Single));

            int n = ctx.Reader.ReadInt32();

            var names = new (string input, string output)[n];
            for (int i = 0; i < n; i++)
            {
                var output = ctx.LoadNonEmptyString();
                var input = ctx.LoadNonEmptyString();
                names[i] = (input, output);
            }

            _columns = new ColumnInfo[n];
            for (int i = 0; i < n; i++)
            {
                int width = ctx.Reader.ReadInt32();
                _host.CheckDecode(width > 0);
                int height = ctx.Reader.ReadInt32();
                _host.CheckDecode(height > 0);
                var scale = (ResizingKind)ctx.Reader.ReadByte();
                _host.CheckDecode(Enum.IsDefined(typeof(ResizingKind), scale));
                var anchor = (Anchor)ctx.Reader.ReadByte();
                _host.CheckDecode(Enum.IsDefined(typeof(Anchor), anchor));
                _columns[i] = new ColumnInfo(names[i].input, names[i].output, width, height, scale, anchor);
            }
        }

        public static IDataTransform Create(IHostEnvironment env, ModelLoadContext ctx, IDataView input)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));
            env.CheckValue(input, nameof(input));

            var transformer = new ImageResizerTransform(env, ctx);
            return new RowToRowMapperTransform(env, input, transformer.MakeRowMapper(input.Schema));
        }

        public void Save(ModelSaveContext ctx) => SaveContents(_host, ctx, _columns);

        private static void SaveContents(IHostEnvironment env, ModelSaveContext ctx, ColumnInfo[] columns)
        {
            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // int: sizeof(float)
            // int: number of added columns
            // for each added column
            //   int: id of output column name
            //   int: id of input column name

            // for each added column
            //   int: width
            //   int: height
            //   byte: scaling kind

            ctx.Writer.Write(sizeof(float));

            ctx.Writer.Write(columns.Length);
            for (int i = 0; i < columns.Length; i++)
            {
                ctx.SaveNonEmptyString(columns[i].Output);
                ctx.SaveNonEmptyString(columns[i].Input);
            }

            foreach (var col in columns)
            {
                ctx.Writer.Write(col.Width);
                ctx.Writer.Write(col.Height);
                env.Assert((ResizingKind)(byte)col.Scale == col.Scale);
                ctx.Writer.Write((byte)col.Scale);
                env.Assert((Anchor)(byte)col.Anchor == col.Anchor);
                ctx.Writer.Write((byte)col.Anchor);
            }
        }

        public ISchema GetOutputSchema(ISchema inputSchema)
        {
            _host.CheckValue(inputSchema, nameof(inputSchema));

            // Check that all the input columns are present and are images.
            foreach (var column in _columns)
                CheckInput(_host, inputSchema, column.Input, out int col);

            return Transform(new EmptyDataView(_host, inputSchema)).Schema;
        }

        public IDataView Transform(IDataView input)
        {
            var mapper = MakeRowMapper(input.Schema);
            return new RowToRowMapperTransform(_host, input, mapper);
        }

        private IRowMapper MakeRowMapper(ISchema schema)
            => new Mapper(_host, _columns, schema);

        private static void CheckInput(IExceptionContext ctx, ISchema inputSchema, string input, out int srcCol)
        {
            Contracts.AssertValueOrNull(ctx);
            Contracts.AssertValue(inputSchema);
            Contracts.AssertNonEmpty(input);

            if (!inputSchema.TryGetColumnIndex(input, out srcCol))
                throw ctx.ExceptSchemaMismatch(nameof(inputSchema), "input", input);
            if (!(inputSchema.GetColumnType(srcCol) is ImageType))
                throw ctx.ExceptSchemaMismatch(nameof(inputSchema), "input", input, "image", inputSchema.GetColumnType(srcCol).ToString());
        }

        internal sealed class Mapper : IRowMapper
        {
            private readonly IHost _host;
            private readonly ColumnInfo[] _columns;
            private readonly ISchema _inputSchema;
            private readonly Dictionary<int, int> _colMapNewToOld;

            public Mapper(IHostEnvironment env, ColumnInfo[] columns, ISchema inputSchema)
            {
                Contracts.AssertValue(env);
                _host = env.Register(nameof(Mapper));
                _host.AssertValue(columns);
                _host.AssertValue(inputSchema);

                _colMapNewToOld = new Dictionary<int, int>();
                for (int i = 0; i < columns.Length; i++)
                {
                    CheckInput(_host, inputSchema, columns[i].Input, out int srcCol);
                    _colMapNewToOld.Add(i, srcCol);
                }
                _columns = columns;
                _inputSchema = inputSchema;
            }

            public Delegate[] CreateGetters(IRow input, Func<int, bool> activeOutput, out Action disposer)
            {
                _host.Assert(input.Schema == _inputSchema);
                var result = new Delegate[_columns.Length];
                var disposers = new Action[_columns.Length];
                for (int i = 0; i < _columns.Length; i++)
                {
                    if (!activeOutput(i))
                        continue;
                    int srcCol = _colMapNewToOld[i];
                    result[i] = MakeGetter(input, i, out disposers[i]);
                }
                disposer = () =>
                {
                    foreach (var act in disposers)
                        act();
                };
                return result;
            }

            public Func<int, bool> GetDependencies(Func<int, bool> activeOutput)
            {
                var active = new bool[_inputSchema.ColumnCount];
                foreach (var pair in _colMapNewToOld)
                    if (activeOutput(pair.Key))
                        active[pair.Value] = true;
                return col => active[col];
            }

            public RowMapperColumnInfo[] GetOutputColumns()
                => _columns.Select(x => new RowMapperColumnInfo(x.Output, x.Type, null)).ToArray();

            public void Save(ModelSaveContext ctx) => SaveContents(_host, ctx, _columns);

            public static IRowMapper Create(IHostEnvironment env, ModelLoadContext ctx, ISchema inputSchema)
            {
                Contracts.CheckValue(env, nameof(env));
                env.CheckValue(ctx, nameof(ctx));
                env.CheckValue(inputSchema, nameof(inputSchema));
                var transformer = new ImageResizerTransform(env, ctx);
                return transformer.MakeRowMapper(inputSchema);
            }

            private Delegate MakeGetter(IRow input, int iinfo, out Action disposer)
            {
                _host.AssertValue(input);
                _host.Assert(0 <= iinfo && iinfo < _columns.Length);

                var src = default(Bitmap);
                var getSrc = input.GetGetter<Bitmap>(_colMapNewToOld[iinfo]);
                var ex = _columns[iinfo];

                disposer =
                    () =>
                    {
                        if (src != null)
                        {
                            src.Dispose();
                            src = null;
                        }
                    };

                ValueGetter<Bitmap> del =
                    (ref Bitmap dst) =>
                    {
                        if (dst != null)
                            dst.Dispose();

                        getSrc(ref src);
                        if (src == null || src.Height <= 0 || src.Width <= 0)
                            return;
                        if (src.Height == ex.Height && src.Width == ex.Width)
                        {
                            dst = src;
                            return;
                        }

                        int sourceWidth = src.Width;
                        int sourceHeight = src.Height;
                        int sourceX = 0;
                        int sourceY = 0;
                        int destX = 0;
                        int destY = 0;
                        int destWidth = 0;
                        int destHeight = 0;
                        float aspect = 0;
                        float widthAspect = 0;
                        float heightAspect = 0;

                        widthAspect = (float)ex.Width / sourceWidth;
                        heightAspect = (float)ex.Height / sourceHeight;

                        if (ex.Scale == ResizingKind.IsoPad)
                        {
                            widthAspect = (float)ex.Width / sourceWidth;
                            heightAspect = (float)ex.Height / sourceHeight;
                            if (heightAspect < widthAspect)
                            {
                                aspect = heightAspect;
                                destX = (int)((ex.Width - (sourceWidth * aspect)) / 2);
                            }
                            else
                            {
                                aspect = widthAspect;
                                destY = (int)((ex.Height - (sourceHeight * aspect)) / 2);
                            }

                            destWidth = (int)(sourceWidth * aspect);
                            destHeight = (int)(sourceHeight * aspect);
                        }
                        else
                        {
                            if (heightAspect < widthAspect)
                            {
                                aspect = widthAspect;
                                switch (ex.Anchor)
                                {
                                    case Anchor.Top:
                                        destY = 0;
                                        break;
                                    case Anchor.Bottom:
                                        destY = (int)(ex.Height - (sourceHeight * aspect));
                                        break;
                                    default:
                                        destY = (int)((ex.Height - (sourceHeight * aspect)) / 2);
                                        break;
                                }
                            }
                            else
                            {
                                aspect = heightAspect;
                                switch (ex.Anchor)
                                {
                                    case Anchor.Left:
                                        destX = 0;
                                        break;
                                    case Anchor.Right:
                                        destX = (int)(ex.Width - (sourceWidth * aspect));
                                        break;
                                    default:
                                        destX = (int)((ex.Width - (sourceWidth * aspect)) / 2);
                                        break;
                                }
                            }

                            destWidth = (int)(sourceWidth * aspect);
                            destHeight = (int)(sourceHeight * aspect);
                        }
                        dst = new Bitmap(ex.Width, ex.Height);
                        var srcRectangle = new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight);
                        var destRectangle = new Rectangle(destX, destY, destWidth, destHeight);
                        using (var g = Graphics.FromImage(dst))
                        {
                            g.DrawImage(src, destRectangle, srcRectangle, GraphicsUnit.Pixel);
                        }
                        _host.Assert(dst.Width == ex.Width && dst.Height == ex.Height);
                    };

                return del;
            }
        }
    }

    public sealed class ImageResizerEstimator : TrivialEstimator<ImageResizerTransform>
    {
        public ImageResizerEstimator(IHostEnvironment env, string inputColumn, string outputColumn,
            int imageWidth, int imageHeight, ImageResizerTransform.ResizingKind resizing = ImageResizerTransform.ResizingKind.IsoCrop, ImageResizerTransform.Anchor cropAnchor = ImageResizerTransform.Anchor.Center)
            : this(env, new ImageResizerTransform(env, inputColumn, outputColumn, imageWidth, imageHeight, resizing, cropAnchor))
        {
        }

        public ImageResizerEstimator(IHostEnvironment env, params ImageResizerTransform.ColumnInfo[] columns)
            : this(env, new ImageResizerTransform(env, columns))
        {
        }

        public ImageResizerEstimator(IHostEnvironment env, ImageResizerTransform transformer)
            : base(Contracts.CheckRef(env, nameof(env)).Register(nameof(ImageResizerEstimator)), transformer)
        {
        }

        public override SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            Host.CheckValue(inputSchema, nameof(inputSchema));
            var result = inputSchema.Columns.ToDictionary(x => x.Name);
            foreach (var colInfo in Transformer.Columns)
            {
                var col = inputSchema.FindColumn(colInfo.Input);

                if (col == null)
                    throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", colInfo.Input);
                if (!(col.ItemType is ImageType) || col.Kind != SchemaShape.Column.VectorKind.Scalar)
                    throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", colInfo.Input, new ImageType().ToString(), col.GetTypeString());

                result[colInfo.Output] = new SchemaShape.Column(colInfo.Output, SchemaShape.Column.VectorKind.Scalar, colInfo.Type, false);
            }

            return new SchemaShape(result.Values);
        }
    }
}
