// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CSharpApp;
using TyphonLib;
using TyphonLib.text;
using static TyphonLib.geometry;

// ── 1. A compiled Typhon library (TyphonLib) used like any other .NET assembly ──────────────
var a = new Vector2(3, 4);
var b = new Vector2(1, 2);
Console.WriteLine($"a + b = {a + b}, a * 2 = {a * 2}, |a| = {a.length}, a·b = {a.dot(b)}, a == (3,4): {a == new Vector2(3, 4)}");

Shape[] shapes =
[
    new Circle(new Vector2(0, 0), 1.5),
    new Polygon([new(0, 0), new(4, 0), new(4, 3)]),
    new LoudSquare(2),
];
foreach (var s in shapes) Console.WriteLine(s.describe());

var hull = convex_hull([new(0, 0), new(2, 0), new(1, 1), new(2, 2), new(0, 2), new(1, 0.5)]);
Console.WriteLine($"convex hull: {string.Join(" ", hull)}  centroid: {centroid(hull)}");

const string text = "The cat and the hat. The cat sat!";
Console.WriteLine($"top words: {string.Join(", ", stats.top_words(text, 2))}");
Console.WriteLine($"palindrome? {stats.is_palindrome("A man, a plan, a canal: Panama")}");

try { _ = new Circle(a, -1); }
catch (ArgumentException e) { Console.WriteLine($"Typhon raised ValueError → C# caught ArgumentException: {e.Message}"); }

// ── 2. Typhon code in *this* project (pricing.ty) that itself uses C# types (TaxRules, IDiscount) ──
var cart = new pricing.Cart("CH").add("book", 12.50m, 2).add("pen", 1.20m, 10);
cart.discounts.Add(new pricing.StudentDiscount());
Console.WriteLine($"subtotal {cart.subtotal():F2}, total incl. VAT after '{cart.discounts[0].Name}': {cart.total():F2}");

// ── 3. A C# class deriving from a Typhon class (Typhon methods are virtual) ──────────────────────
class LoudSquare(double side) : Polygon([new(0, 0), new(side, 0), new(side, side), new(0, side)])
{
    public override string describe() => base.describe().ToUpperInvariant();
}
