// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Internal.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.ML.Runtime.Data
{
    public sealed class Schema: ISchema
    {
        private readonly Column[] _columns;
        private readonly Dictionary<string, int> _nameMap;

        public int ColumnCount => _columns.Length;

        public IColumn this[string name]
        {
            get
            {
                Contracts.CheckValue(name, nameof(name));
                if (!_nameMap.TryGetValue(name, out int col))
                    throw Contracts.ExceptParam(nameof(name), $"Column '{name}' not found");
                return _columns[col];
            }
        }

        public IColumn this[int col]
        {
            get
            {
                Contracts.CheckParam(0 <= col && col < _columns.Length, nameof(col));
                return _columns[col];
            }
        }

        public sealed class Column : IColumn
        {
            public string Name { get; }

            public ColumnType Type { get; }

            public IStandaloneRow Metadata { get; }

            public Column(string name, ColumnType type, IStandaloneRow metadata)
            {
                Contracts.CheckNonEmpty(name, nameof(name));
                Contracts.CheckValue(type, nameof(type));
                Contracts.CheckValueOrNull(metadata);

                Name = name;
                Type = type;
                Metadata = metadata;
            }
        }

        public Schema(IEnumerable<Column> columns)
        {
            Contracts.CheckValue(columns, nameof(columns));

            _columns = columns.ToArray();
            _nameMap = new Dictionary<string, int>();
            for (int i = 0; i < _columns.Length; i++)
                _nameMap[_columns[i].Name] = i;
        }

        public bool TryGetColumnIndex(string name, out int col)
            => _nameMap.TryGetValue(name, out col);

        public string GetColumnName(int col) => this[col].Name;

        public ColumnType GetColumnType(int col) => this[col].Type;

        public IEnumerable<KeyValuePair<string, ColumnType>> GetMetadataTypes(int col)
        {
            var meta = this[col].Metadata;
            if (meta == null)
                throw MetadataUtils.ExceptGetMetadata();
            return meta.Schema.GetColumns().Select(c => new KeyValuePair<string, ColumnType>(c.column.Name, c.column.Type));
        }

        public ColumnType GetMetadataTypeOrNull(string kind, int col)
        {
            var meta = this[col].Metadata;
            if (meta == null)
                throw MetadataUtils.ExceptGetMetadata();
            return meta.Schema[kind].Type;
        }

        public void GetMetadata<TValue>(string kind, int col, ref TValue value)
        {
            var meta = this[col].Metadata;
            if (meta == null)
                throw MetadataUtils.ExceptGetMetadata();
            if (!meta.Schema.TryGetColumnIndex(kind, out int metaCol))
                throw MetadataUtils.ExceptGetMetadata();
            meta.GetValue(metaCol, ref value);
        }

        public IEnumerable<(int index, IColumn column)> GetColumns() => _nameMap.Values.Select(idx => (idx, (IColumn)_columns[idx]));
    }

    public sealed class MetadataRow : IStandaloneRow
    {
        private readonly (Schema.Column column, Delegate getter)[] _values;
        public ISchema Schema { get; }

        public MetadataRow(IEnumerable<(Schema.Column column, Delegate getter)> values)
        {
            Contracts.CheckValue(values, nameof(values));
            // Check all getters.
            foreach (var (column, getter) in values)
            {
                Contracts.CheckValue(column, nameof(column));
                Contracts.CheckValue(getter, nameof(getter));
                Utils.MarshalActionInvoke(CheckGetter<int>, column.Type.RawType, getter);
            }
            _values = values.ToArray();
            Schema = new Schema(_values.Select(x => x.column));
        }

        private void CheckGetter<TValue>(Delegate getter)
        {
            var typedGetter = getter as ValueGetter<TValue>;
            if (typedGetter == null)
                throw Contracts.ExceptParam(nameof(getter), $"Getter of type '{typeof(TValue)}' expected, but {getter.GetType()} found");
        }

        public void GetValue<TValue>(int col, ref TValue value)
        {
            Contracts.CheckParam(0 <= col && col < _values.Length, nameof(col));
            var typedGetter = _values[col].getter as ValueGetter<TValue>;
            typedGetter(ref value);
        }
    }
}
