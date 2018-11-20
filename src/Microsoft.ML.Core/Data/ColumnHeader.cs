// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Data;

namespace Microsoft.ML.Data
{
    /// <summary>
    /// This class represents the schema of one column of a data view, without an attachment to a particular <see cref="Schema"/>.
    /// </summary>
    public sealed class ColumnHeader
    {
        /// <summary>
        /// The name of the column.
        /// </summary>
        public string Name { get; }
        /// <summary>
        /// The type of the column.
        /// </summary>
        public ColumnType Type { get; }
        /// <summary>
        /// The metadata associated with the column.
        /// </summary>
        public Schema.Metadata Metadata { get; }

        /// <summary>
        /// Creates an instance of a <see cref="ColumnHeader"/>.
        /// </summary>
        public ColumnHeader(string name, ColumnType type, Schema.Metadata metadata)
        {
            Contracts.CheckNonEmpty(name, nameof(name));
            Contracts.CheckValue(type, nameof(type));
            Contracts.CheckValueOrNull(metadata);
            Name = name;
            Type = type;
            Metadata = metadata ?? Schema.Metadata.Empty;
        }

        public ColumnHeader(Schema.Column column)
        {
            Contracts.CheckValue(column, nameof(column));
            Name = column.Name;
            Type = column.Type;
            Metadata = column.Metadata;
        }
    }
}
