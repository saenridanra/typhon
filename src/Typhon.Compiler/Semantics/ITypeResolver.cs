// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Typhon.Compiler.Semantics
{
    /// <summary>
    /// Answers questions about .NET symbols the Typhon compiler cannot know from Typhon source alone
    /// (e.g. "is System.Text.StringBuilder a type?" — needed because Python has no 'new').
    /// Implemented on top of a Roslyn Compilation, so it sees referenced assemblies and C# code in the same project.
    /// </summary>
    public interface ITypeResolver
    {
        /// <summary>CLR metadata name, e.g. "System.Collections.Generic.List`1" or "Outer+Inner".</summary>
        bool TypeExists(string metadataName);
        bool NamespaceExists(string dottedNamespace);
        /// <summary>Does the type (or one of its base types) declare a member with that name?</summary>
        bool HasMember(string metadataName, string memberName);
        /// <summary>Is there a virtual/abstract/override member with that name in the type or its bases?</summary>
        bool IsOverridable(string metadataName, string memberName);
        /// <summary>Public and protected instance constructors of a non-generic class (empty when unknown).</summary>
        IReadOnlyList<CtorSignature> GetConstructors(string metadataName);
    }

    /// <summary>A constructor as the emitter needs it: fully qualified C# parameter types and names.</summary>
    public sealed class CtorSignature
    {
        public bool IsProtected;
        public List<KeyValuePair<string, string>> Parameters = new List<KeyValuePair<string, string>>();
    }

    /// <summary>Resolver used when no compilation is available (unit tests, syntax-only tools).</summary>
    public sealed class NullTypeResolver : ITypeResolver
    {
        public static readonly NullTypeResolver Instance = new NullTypeResolver();
        public bool TypeExists(string metadataName) => false;
        public bool NamespaceExists(string dottedNamespace) => false;
        public bool HasMember(string metadataName, string memberName) => false;
        public bool IsOverridable(string metadataName, string memberName) => false;
        public IReadOnlyList<CtorSignature> GetConstructors(string metadataName) => new CtorSignature[0];
    }
}
