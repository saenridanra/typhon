// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;

// The runtime's types stay internal (exactly as when embedded into a program); scripts compiled in memory by
// `typhon run` always get this assembly name, so they see them.
[assembly: InternalsVisibleTo(Typhon.Runtime.RuntimeAssembly.ScriptAssemblyName)]

namespace Typhon.Runtime
{
    internal static class RuntimeAssembly
    {
        /// <summary>Assembly name of every script compiled in memory against the precompiled runtime.</summary>
        public const string ScriptAssemblyName = "typhon_script";
    }
}
