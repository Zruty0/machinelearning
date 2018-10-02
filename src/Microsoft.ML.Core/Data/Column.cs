// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Runtime.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.ML.Runtime.Data
{
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

    public sealed class MetadataRow: IStandaloneRow
    {
        public abstract class ValueBase: IColumn
        {
            public string Name { get; }
            public ColumnType Type { get; }
            public IStandaloneRow Metadata { get; }

            protected ValueBase(string name, ColumnType type, IStandaloneRow metadata)
            {
                Name = name;
                Type = type;
                Metadata = metadata;
            }
        }

        public sealed class Value<T>: ValueBase
        {
            public ValueGetter<T> Getter { get; }

            public Value(ValueGetter<T> getter)
            {

            }
        }
    }
}
