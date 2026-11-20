// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace CSharpApp;

/// <summary>C# types used by Typhon code in the same project (pricing.ty).</summary>
public interface IDiscount
{
    string Name { get; }
    decimal Apply(decimal amount);
}

public static class TaxRules
{
    public static decimal RateFor(string country) => country switch
    {
        "CH" => 0.081m,
        "DE" => 0.19m,
        _ => 0.20m,
    };
}
