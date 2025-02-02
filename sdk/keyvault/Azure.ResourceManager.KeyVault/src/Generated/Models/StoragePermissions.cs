// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// <auto-generated/>

#nullable disable

using System;
using System.ComponentModel;

namespace Azure.ResourceManager.KeyVault.Models
{
    /// <summary> The StoragePermissions. </summary>
    public readonly partial struct StoragePermissions : IEquatable<StoragePermissions>
    {
        private readonly string _value;

        /// <summary> Initializes a new instance of <see cref="StoragePermissions"/>. </summary>
        /// <exception cref="ArgumentNullException"> <paramref name="value"/> is null. </exception>
        public StoragePermissions(string value)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
        }

        private const string GetValue = "get";
        private const string ListValue = "list";
        private const string DeleteValue = "delete";
        private const string SetValue = "set";
        private const string UpdateValue = "update";
        private const string RegeneratekeyValue = "regeneratekey";
        private const string RecoverValue = "recover";
        private const string PurgeValue = "purge";
        private const string BackupValue = "backup";
        private const string RestoreValue = "restore";
        private const string SetsasValue = "setsas";
        private const string ListsasValue = "listsas";
        private const string GetsasValue = "getsas";
        private const string DeletesasValue = "deletesas";

        /// <summary> get. </summary>
        public static StoragePermissions Get { get; } = new StoragePermissions(GetValue);
        /// <summary> list. </summary>
        public static StoragePermissions List { get; } = new StoragePermissions(ListValue);
        /// <summary> delete. </summary>
        public static StoragePermissions Delete { get; } = new StoragePermissions(DeleteValue);
        /// <summary> set. </summary>
        public static StoragePermissions Set { get; } = new StoragePermissions(SetValue);
        /// <summary> update. </summary>
        public static StoragePermissions Update { get; } = new StoragePermissions(UpdateValue);
        /// <summary> regeneratekey. </summary>
        public static StoragePermissions Regeneratekey { get; } = new StoragePermissions(RegeneratekeyValue);
        /// <summary> recover. </summary>
        public static StoragePermissions Recover { get; } = new StoragePermissions(RecoverValue);
        /// <summary> purge. </summary>
        public static StoragePermissions Purge { get; } = new StoragePermissions(PurgeValue);
        /// <summary> backup. </summary>
        public static StoragePermissions Backup { get; } = new StoragePermissions(BackupValue);
        /// <summary> restore. </summary>
        public static StoragePermissions Restore { get; } = new StoragePermissions(RestoreValue);
        /// <summary> setsas. </summary>
        public static StoragePermissions Setsas { get; } = new StoragePermissions(SetsasValue);
        /// <summary> listsas. </summary>
        public static StoragePermissions Listsas { get; } = new StoragePermissions(ListsasValue);
        /// <summary> getsas. </summary>
        public static StoragePermissions Getsas { get; } = new StoragePermissions(GetsasValue);
        /// <summary> deletesas. </summary>
        public static StoragePermissions Deletesas { get; } = new StoragePermissions(DeletesasValue);
        /// <summary> Determines if two <see cref="StoragePermissions"/> values are the same. </summary>
        public static bool operator ==(StoragePermissions left, StoragePermissions right) => left.Equals(right);
        /// <summary> Determines if two <see cref="StoragePermissions"/> values are not the same. </summary>
        public static bool operator !=(StoragePermissions left, StoragePermissions right) => !left.Equals(right);
        /// <summary> Converts a string to a <see cref="StoragePermissions"/>. </summary>
        public static implicit operator StoragePermissions(string value) => new StoragePermissions(value);

        /// <inheritdoc />
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool Equals(object obj) => obj is StoragePermissions other && Equals(other);
        /// <inheritdoc />
        public bool Equals(StoragePermissions other) => string.Equals(_value, other._value, StringComparison.InvariantCultureIgnoreCase);

        /// <inheritdoc />
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override int GetHashCode() => _value?.GetHashCode() ?? 0;
        /// <inheritdoc />
        public override string ToString() => _value;
    }
}
