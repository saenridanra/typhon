// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Typhon.Compiler.Semantics
{
    public sealed class RoslynTypeResolver : ITypeResolver
    {
        readonly Compilation _compilation;
        readonly Dictionary<string, INamedTypeSymbol> _types = new Dictionary<string, INamedTypeSymbol>();
        readonly Dictionary<string, bool> _namespaces = new Dictionary<string, bool>();

        public RoslynTypeResolver(Compilation compilation) { _compilation = compilation; }

        INamedTypeSymbol Find(string metadataName)
        {
            if (_types.TryGetValue(metadataName, out var cached)) return cached;
            var t = _compilation.GetTypeByMetadataName(metadataName);
            if (t == null)
            {
                // Ambiguous across references (GetTypeByMetadataName returns null) — pick the first public one.
                foreach (var asm in _compilation.SourceModule.ReferencedAssemblySymbols)
                {
                    var candidate = asm.GetTypeByMetadataName(metadataName);
                    if (candidate != null && candidate.DeclaredAccessibility == Accessibility.Public) { t = candidate; break; }
                }
            }
            _types[metadataName] = t;
            return t;
        }

        public bool TypeExists(string metadataName) => Find(metadataName) != null;

        public bool NamespaceExists(string dottedNamespace)
        {
            if (_namespaces.TryGetValue(dottedNamespace, out var known)) return known;
            INamespaceSymbol ns = _compilation.GlobalNamespace;
            foreach (var part in dottedNamespace.Split('.'))
            {
                ns = ns.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                if (ns == null) break;
            }
            return _namespaces[dottedNamespace] = ns != null;
        }

        // Interfaces are implemented, not inherited: their members must be declared (not hidden, not overridden).
        public bool HasMember(string metadataName, string memberName)
        {
            var start = Find(metadataName);
            if (start == null || start.TypeKind == TypeKind.Interface) return false;
            for (ITypeSymbol t = start; t != null; t = t.BaseType)
                if (t.GetMembers(memberName).Length > 0) return true;
            return false;
        }

        public bool IsOverridable(string metadataName, string memberName)
        {
            var start = Find(metadataName);
            if (start == null || start.TypeKind == TypeKind.Interface) return false;
            for (ITypeSymbol t = start; t != null; t = t.BaseType)
                foreach (var m in t.GetMembers(memberName))
                    if ((m.IsVirtual || m.IsAbstract || m.IsOverride) && !m.IsSealed) return true;
            return false;
        }
    }
}
