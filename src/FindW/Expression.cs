namespace WinUtils.FindW;

/// <summary>A node of a find expression, evaluated once per file.</summary>
internal abstract class Expr
{
    public abstract bool Eval(FileEntry e);
}

internal sealed class AndExpr(Expr left, Expr right) : Expr
{
    public override bool Eval(FileEntry e) => left.Eval(e) && right.Eval(e);
}

internal sealed class OrExpr(Expr left, Expr right) : Expr
{
    public override bool Eval(FileEntry e) => left.Eval(e) || right.Eval(e);
}

internal sealed class NotExpr(Expr inner) : Expr
{
    public override bool Eval(FileEntry e) => !inner.Eval(e);
}

/// <summary>"a , b": evaluates both and returns the value of b.</summary>
internal sealed class CommaExpr(Expr left, Expr right) : Expr
{
    public override bool Eval(FileEntry e)
    {
        left.Eval(e);
        return right.Eval(e);
    }
}

/// <summary>A test or action.</summary>
internal sealed class Primary(Func<FileEntry, bool> eval) : Expr
{
    public static readonly Primary True = new(_ => true);
    public static readonly Primary False = new(_ => false);

    public override bool Eval(FileEntry e) => eval(e);
}
