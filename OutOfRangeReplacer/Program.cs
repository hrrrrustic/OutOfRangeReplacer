// See https://aka.ms/new-console-template for more information

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Host;
using OutOfRangeReplacer;

Console.WriteLine("Hello, World!");


var path = @"D:\Development\OpenSource\runtime\src\libraries";

foreach (String file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
{
    if (file.Contains("\\ref\\") || file.Contains("\\tests\\") || file.Contains("asn.xml", StringComparison.OrdinalIgnoreCase) || file.Contains("asn1", StringComparison.OrdinalIgnoreCase))
        continue;
    
    if (file.Contains("ArgumentOutOfRangeException"))
        continue;
    
    var content = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(content);
    var compilation = CSharpCompilation.Create(null).AddSyntaxTrees(tree);
    var root = tree.GetRoot();
    var semantic = compilation.GetSemanticModel(tree);
    var visitor = new IfWithZeroCheckVisitor(semantic);
    visitor.Visit(root);

    if (visitor.Simplifies.Count != 0)
    {
        foreach (var (old, fixes) in visitor.Simplifies)
        {
            var forInsert = root.DescendantNodes().First(k => k.IsEquivalentTo(old));
            root = root.InsertNodesAfter(forInsert, fixes);
            var forRemove = root.DescendantNodes().First(k => k.IsEquivalentTo(old));
            root = root.RemoveNode(forRemove, SyntaxRemoveOptions.KeepDirectives);
        }

        File.WriteAllText(file, root.ToFullString());
    }
    else if (visitor.Fixes.Count != 0)
    {
        root = root.ReplaceNodes(visitor.Fixes.Select(k => k.old), (node, syntaxNode) => visitor.Fixes.First(e => e.old == node).fix);
        File.WriteAllText(file, root.ToFullString());
    }
}