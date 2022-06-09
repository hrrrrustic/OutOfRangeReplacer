using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OutOfRangeReplacer;


public enum ReplaceMethod
{
    Negative,
    NegativeOrZero,
    LessThan,
    GreaterThan
}

public class IfWithZeroCheckVisitor : CSharpSyntaxWalker
{
    private const string ThrowIfNegative = "ArgumentOutOfRangeException.ThrowIfNegative";
    private const string ThrowIfLessThan = "ArgumentOutOfRangeException.ThrowIfLessThan";
    private const string ThrowIfGreaterThan = "ArgumentOutOfRangeException.ThrowIfGreaterThan";
    private const string ThrowIfNegativeOrZero = "ArgumentOutOfRangeException.ThrowIfNegativeOrZero";
    public List<(SyntaxNode old, SyntaxNode fix)> Fixes = new ();
    public List<(SyntaxNode old, List<SyntaxNode> fix)> Simplifies = new(); 
    private readonly SyntaxKind[] SupportedOps = new[]
        {SyntaxKind.LessThanToken, SyntaxKind.LessThanEqualsToken, SyntaxKind.GreaterThanToken};

    private readonly SemanticModel _semantic;

    public IfWithZeroCheckVisitor(SemanticModel semantic, SyntaxWalkerDepth depth = SyntaxWalkerDepth.Node) : base(depth)
    {
        _semantic = semantic;
    }

    private ExpressionStatementSyntax GetThrowIfWithZeroExpression(string expression, ReplaceMethod method)
    {
        string prefix = method is ReplaceMethod.NegativeOrZero ? ThrowIfNegativeOrZero : ThrowIfNegative;
        
        return (ExpressionStatementSyntax) SyntaxFactory.ParseStatement(prefix + $"({expression.Trim()});");
    }
    
    private ExpressionStatementSyntax GetThrowIfExpression(string firstArg, string secondArg, ReplaceMethod method)
    {
        string prefix = method is ReplaceMethod.LessThan ? ThrowIfLessThan : ThrowIfGreaterThan;
        
        return (ExpressionStatementSyntax) SyntaxFactory.ParseStatement(prefix + $"({firstArg.Trim()}, {secondArg.Trim()});");
    }

    private ReplaceMethod? GetReplaceMethod(ExpressionSyntax rightPart, SyntaxToken op)
    {
        if (rightPart is LiteralExpressionSyntax {Token.Text: "0"})
        {
            if (op.IsKind(SyntaxKind.LessThanToken))
                return ReplaceMethod.Negative;
                
            if (op.IsKind(SyntaxKind.LessThanEqualsToken))
                return ReplaceMethod.NegativeOrZero;
        }
        

        if (op.IsKind(SyntaxKind.LessThanToken))
            return ReplaceMethod.LessThan;
                
        if (op.IsKind(SyntaxKind.GreaterThanToken))
            return ReplaceMethod.GreaterThan;
        
        return null;
    }

    private void AddFix(IfStatementSyntax ifStatementSyntax, BinaryExpressionSyntax binary, ReplaceMethod method)
    {
        if (method is ReplaceMethod.Negative or ReplaceMethod.NegativeOrZero)
            Fixes.Add((ifStatementSyntax, GetThrowIfWithZeroExpression(binary.Left.ToFullString(), method).WithTriviaFrom(ifStatementSyntax)));
       
        Fixes.Add((ifStatementSyntax, GetThrowIfExpression(binary.Left.ToFullString(), binary.Right.ToFullString(), method).WithTriviaFrom(ifStatementSyntax)));
    }

    private SyntaxNode GetNewIf(string ifValue, string throwValue)
    {
        var result = $@"
if ({ifValue}) throw new ArgumentOutOfRangeException({throwValue};";

        return (IfStatementSyntax) SyntaxFactory.ParseStatement(result);
    }
    private void Simplify(IfStatementSyntax ifStatementSyntax, BinaryExpressionSyntax left, BinaryExpressionSyntax right, ConditionalExpressionSyntax cond)
    {
        var res1 = GetNewIf(left.ToFullString(), cond.WhenTrue.ToFullString()).WithTriviaFrom(ifStatementSyntax);
        var res2 = GetNewIf(right.ToFullString(), cond.WhenFalse.ToFullString()).WithTriviaFrom(ifStatementSyntax);

        var list = new List<SyntaxNode>() {res1, res2};
        
        Simplifies.Add((ifStatementSyntax, list));
    }
    
    private void SimplifyDoubleZeroCheck(IfStatementSyntax ifStatementSyntax, BinaryExpressionSyntax left, BinaryExpressionSyntax right)
    {
        if (!(left.OperatorToken.IsKind(SyntaxKind.LessThanToken) && right.OperatorToken.IsKind(SyntaxKind.LessThanToken)))
            return;
        
        ThrowStatementSyntax? throwing = null;
        if (ifStatementSyntax.Statement is BlockSyntax {Statements.Count: 1} block && block.Statements.First() is ThrowStatementSyntax throwing1)
            throwing = throwing1;

        if (ifStatementSyntax.Statement is ThrowStatementSyntax throwing2)
            throwing = throwing2;
        
        if (throwing is null)
            return;

        var conditional = throwing.DescendantNodes().OfType<ConditionalExpressionSyntax>().SingleOrDefault();
        
        if (conditional is null)
            return;
        
        Simplify(ifStatementSyntax, left, right, conditional);
    }


    private static readonly Regex _enum = new Regex("[a-zA-Z0-9]+\\.[a-zA-Z0-9]+", RegexOptions.Compiled);
    private static readonly Regex _minMax = new Regex("[a-zA-Z0-9]+\\.((Min)|(Max))Value");
    private SyntaxNode? GetIsBetweenNode(string firstArg, string secondArg, string thirdArg)
    {
        var first = firstArg.Trim();
        var second = secondArg.Trim();
        var third = thirdArg.Trim();

        var secMatch = _enum.Matches(second);
        var thirdMatch = _enum.Matches(third);

        bool isEnum = secMatch.Count == 1 && secMatch[0].Value == second && thirdMatch.Count == 1 && thirdMatch[0].Value == third;

        var minMaxLeft = _minMax.Count(second);
        var minMaxRight = _minMax.Count(third);
        bool isTypeMinMaxValue = minMaxLeft > 0 || minMaxRight > 0;

        if (isTypeMinMaxValue)
            isEnum = false;

        if (isEnum)
            return null;
        var result = $"ArgumentOutOfRangeException.ThrowIfNotBetween({first}, {second}, {third}";
        if (isEnum)
            result += ", \"ENUMUSAGE\"";

        result += ");";
        return (ExpressionStatementSyntax) SyntaxFactory.ParseStatement(result);
    }
    
    private void ProcessIsBetween(IfStatementSyntax ifStatementSyntax, BinaryExpressionSyntax left, BinaryExpressionSyntax right)
    {
        if (left.Left.ToFullString().Trim() != right.Left.ToFullString().Trim())
            return;

        var leftToken = left.OperatorToken;
        var rightToken = right.OperatorToken;
        
        if (!((leftToken.IsKind(SyntaxKind.LessThanToken) && rightToken.IsKind(SyntaxKind.GreaterThanToken)) || (leftToken.IsKind(SyntaxKind.GreaterThanToken) && rightToken.IsKind(SyntaxKind.LessThanToken))))
            return;

        bool swap = leftToken.IsKind(SyntaxKind.GreaterThanToken);
        
        ThrowStatementSyntax? throwing = null;
        if (ifStatementSyntax.Statement is BlockSyntax {Statements.Count: 1} block && block.Statements.First() is ThrowStatementSyntax throwing1 && ProceedThrowStatement(throwing1))
            throwing = throwing1;

        if (ifStatementSyntax.Statement is ThrowStatementSyntax throwing2 && ProceedThrowStatement(throwing2))
            throwing = throwing2;
        
        if (throwing is null)
            return;

        string leftValue = null;
        string rightValue = null;

        if (swap)
        {
            leftValue = right.Right.ToFullString();
            rightValue = left.Right.ToFullString();
        }
        else
        {
            leftValue = left.Right.ToFullString();
            rightValue = right.Right.ToFullString();
        }
        
        var replacement =
            GetIsBetweenNode(left.Left.ToFullString(), leftValue, rightValue)?.WithTriviaFrom(ifStatementSyntax);
        
        if(replacement is null)
            return;
        
        Fixes.Add((ifStatementSyntax, replacement));
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        base.VisitIfStatement(node);
        if(node.Parent is ElseClauseSyntax or LabeledStatementSyntax)
            return;
        
        if(node.Else is not null)
            return;
        
        if (node.Condition is not BinaryExpressionSyntax binary)
            return;

        if (binary is {Left: BinaryExpressionSyntax left, Right: BinaryExpressionSyntax right, OperatorToken.Text: "||"})
        {
            if (left.Right is LiteralExpressionSyntax {Token.Text: "0"} && right.Right is LiteralExpressionSyntax {Token.Text: "0"})
                SimplifyDoubleZeroCheck(node, left, right);
            else
                ProcessIsBetween(node, left, right);            
            return;
        }
        
        if (!SupportedOps.Any(k => binary.OperatorToken.IsKind(k)))
            return;
        
        var replaceMethod = GetReplaceMethod(binary.Right, binary.OperatorToken);
        
        if(replaceMethod is null)
            return;

        var replace = replaceMethod.Value;
        if (node.Statement is BlockSyntax blockSyntax)
        {
            var result = ProceedIfWithBrackets(blockSyntax);
            if (result)
                AddFix(node, binary, replace);
            return;
        }

        var result2 = ProceedIfWithoutBrackets(node.Statement);

        if (result2)
            AddFix(node, binary, replace);
    }
    
    private bool ProceedPossibleThrowHelper(ExpressionStatementSyntax expressionStatementSyntax)
    {
        return false;
    }
    private bool ProceedThrowStatement(ThrowStatementSyntax throwStatementSyntax)
    {
        if (throwStatementSyntax.Expression is not ObjectCreationExpressionSyntax {Type: IdentifierNameSyntax {Identifier.Text: nameof(ArgumentOutOfRangeException)}} creation)
            return false;

        return creation.ArgumentList is {Arguments.Count: 1 or 2 or 3};
    }

    private bool ProceedIfContent(StatementSyntax ifStatement)
    {
        switch (ifStatement)
        {
            case ThrowStatementSyntax throwStatementSyntax:
                return ProceedThrowStatement(throwStatementSyntax);
            case ExpressionStatementSyntax expressionStatementSyntax:
                return ProceedPossibleThrowHelper(expressionStatementSyntax);
            default: return false;
        }
    }

    private bool ProceedIfWithoutBrackets(StatementSyntax ifStatement) => ProceedIfContent(ifStatement);
    
    private bool ProceedIfWithBrackets(BlockSyntax block)
    {
        if (block.Statements.Count is not 1)
            return false;
        
        return ProceedIfContent(block.Statements.First());
    }
}