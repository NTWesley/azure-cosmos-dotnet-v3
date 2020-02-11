﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using Newtonsoft.Json;

    /// <summary>
    /// Metadata that a key wrapping provider can use to wrap/unwrap data encryption keys.
    /// <seealso cref="EncryptionKeyWrapProvider" />
    /// </summary>
#if PREVIEW
    public
#else
    internal
#endif
         class EncryptionKeyWrapMetadata : IEquatable<EncryptionKeyWrapMetadata>
    {
        // For JSON deserialize
        private EncryptionKeyWrapMetadata()
        {
        }

        /// <summary>
        /// Creates a new instance of key wrap metadata.
        /// </summary>
        /// <param name="value">Value of the metadata.</param>
        public EncryptionKeyWrapMetadata(string value)
        {
            this.Type = "custom";
            this.Value = value;
        }

        /// <summary>
        /// Creates a new instance of key wrap metadata based on an existing instance.
        /// </summary>
        /// <param name="source">Existing instance from which to initialize.</param>
        public EncryptionKeyWrapMetadata(EncryptionKeyWrapMetadata source)
        {
            this.Type = source.Type;
            this.Algorithm = source.Algorithm;
            this.Value = source.Value;
        }

        [JsonProperty(PropertyName = "type", NullValueHandling = NullValueHandling.Ignore)]
        internal string Type { get; set; }

        [JsonProperty(PropertyName = "algorithm", NullValueHandling = NullValueHandling.Ignore)]
        internal string Algorithm { get; set; }

        /// <summary>
        /// Serialized form of metadata.
        /// Note: This value is saved in the Cosmos DB service.
        /// Implementors of derived implementations should ensure that this does not have (private) key material or credential information.
        /// </summary>
        [JsonProperty(PropertyName = "value", NullValueHandling = NullValueHandling.Ignore)]
        public string Value { get; private set; }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            EncryptionKeyWrapMetadata metadata = obj as EncryptionKeyWrapMetadata;
            return this.Equals(metadata);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            int hashCode = 1265339359;
            hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Type);
            hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Algorithm);
            hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Value);
            return hashCode;
        }

        /// <summary>
        /// Returns whether the properties of the passed in key wrap metadata matches with those in the current instance.
        /// </summary>
        /// <param name="other">Key wrap metadata to be compared with current instance.</param>
        /// <returns>
        /// True if the properties of the key wrap metadata passed in matches with those in the current instance, else false.
        /// </returns>
        public bool Equals(EncryptionKeyWrapMetadata other)
        {
            return other != null &&
                   this.Type == other.Type &&
                   this.Algorithm == other.Algorithm &&
                   this.Value == other.Value;
        }
    }
}