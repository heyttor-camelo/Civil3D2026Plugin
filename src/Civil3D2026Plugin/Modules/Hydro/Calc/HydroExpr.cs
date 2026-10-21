using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Civil3D2026Plugin.Modules.Hydro.Calc;

/// <summary>Erro de expressao com mensagem para o usuario.</summary>
public sealed class HydroExprException : Exception
{
    public HydroExprException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Expressoes das regras das estruturas (§9 do plano): numeros, variaveis, + - * / ^, parenteses,
/// comparacoes e funcoes. Decimal com virgula ou ponto (0,15 = 0.15); argumentos separados por
/// ponto e virgula, como no Excel em portugues: max(dmax; 0,60). Nomes sem diferenca de maiusculas.
/// </summary>
public sealed class HydroExpr
{
    private abstract class Node
    {
        public abstract double Eval(Func<string, double?> vars);
    }

    private sealed class Num : Node
    {
        public double Value;
        public override double Eval(Func<string, double?> vars) => Value;
    }

    private sealed class Var : Node
    {
        public string Name = "";
        public override double Eval(Func<string, double?> vars) =>
            vars(Name) is double v && !double.IsNaN(v) ? v : throw new HydroExprException($"variavel '{Name}' sem valor");
    }

    private sealed class Unary : Node
    {
        public char Op;
        public Node Arg = null!;
        public override double Eval(Func<string, double?> vars) => Op == '-' ? -Arg.Eval(vars) : Arg.Eval(vars);
    }

    private sealed class Binary : Node
    {
        public string Op = "";
        public Node A = null!, B = null!;

        public override double Eval(Func<string, double?> vars)
        {
            double a = A.Eval(vars), b = B.Eval(vars);
            return Op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => Math.Abs(b) < 1e-15 ? throw new HydroExprException("divisao por zero") : a / b,
                "^" => Math.Pow(a, b),
                ">" => a > b + 1e-9 ? 1 : 0,
                "<" => a < b - 1e-9 ? 1 : 0,
                ">=" => a >= b - 1e-9 ? 1 : 0,
                "<=" => a <= b + 1e-9 ? 1 : 0,
                "==" or "=" => Math.Abs(a - b) <= 1e-9 ? 1 : 0,
                "!=" or "<>" => Math.Abs(a - b) > 1e-9 ? 1 : 0,
                _ => throw new HydroExprException("operador " + Op)
            };
        }
    }

    private sealed class Call : Node
    {
        public string Name = "";
        public List<Node> Args = new();

        public override double Eval(Func<string, double?> vars)
        {
            if (Name == "se")
            {
                Need(3, 3);
                return Math.Abs(Args[0].Eval(vars)) > 1e-12 ? Args[1].Eval(vars) : Args[2].Eval(vars);
            }

            double[] a = Args.Select(x => x.Eval(vars)).ToArray();
            switch (Name)
            {
                case "max":
                    Need(1, 99);
                    return a.Max();
                case "min":
                    Need(1, 99);
                    return a.Min();
                case "abs":
                    Need(1, 1);
                    return Math.Abs(a[0]);
                case "raiz":
                case "sqrt":
                    Need(1, 1);
                    return a[0] < 0 ? throw new HydroExprException("raiz de numero negativo") : Math.Sqrt(a[0]);
                case "arred":
                case "round":
                    Need(1, 2);
                    return Math.Round(a[0], a.Length > 1 ? (int)a[1] : 0, MidpointRounding.AwayFromZero);
                case "teto":
                case "ceil":
                    Need(1, 2);
                    return Step(a, Math.Ceiling);
                case "piso":
                case "floor":
                    Need(1, 2);
                    return Step(a, Math.Floor);
                case "limitar":
                case "clamp":
                    Need(3, 3);
                    return Math.Min(Math.Max(a[0], a[1]), a[2]);
                default:
                    throw new HydroExprException($"funcao '{Name}' desconhecida (use max, min, abs, raiz, arred, teto, piso, limitar, se)");
            }
        }

        private void Need(int min, int max)
        {
            if (Args.Count < min || Args.Count > max)
                throw new HydroExprException($"{Name}: {(min == max ? min.ToString(CultureInfo.InvariantCulture) : min + " a " + max)} argumento(s), separados por ';'");
        }

        /// <summary>teto(x; passo) / piso(x; passo): arredonda para o multiplo do passo (padrao 1).</summary>
        private static double Step(double[] a, Func<double, double> f)
        {
            double step = a.Length > 1 ? a[1] : 1;
            if (step <= 0)
                throw new HydroExprException("passo do arredondamento deve ser positivo");
            // Tira o ruido de ponto flutuante antes (0,7 / 0,1 = 6,9999999).
            return f(Math.Round(a[0] / step, 9)) * step;
        }
    }

    private readonly Node _root;

    /// <summary>Texto original da expressao.</summary>
    public string Text { get; }

    /// <summary>Variaveis usadas (minusculas).</summary>
    public IReadOnlyCollection<string> Variables { get; }

    private HydroExpr(string text, Node root, HashSet<string> vars)
    {
        Text = text;
        _root = root;
        Variables = vars;
    }

    /// <summary>Le a expressao; erro de sintaxe vira HydroExprException com a posicao.</summary>
    public static HydroExpr Parse(string text)
    {
        string t = (text ?? "").Trim();
        if (t.StartsWith('='))
            t = t[1..].Trim();
        if (t.Length == 0)
            throw new HydroExprException("expressao vazia");
        Parser p = new(t);
        Node root = p.ParseAll();
        return new HydroExpr(t, root, p.Vars);
    }

    public static bool TryParse(string text, out HydroExpr? expr, out string error)
    {
        try
        {
            expr = Parse(text);
            error = "";
            return true;
        }
        catch (HydroExprException ex)
        {
            expr = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Avalia; vars recebe o nome em minusculas e devolve null quando nao existe.</summary>
    public double Evaluate(Func<string, double?> vars)
    {
        foreach (string v in Variables)
        {
            if (vars(v) is null)
                throw new HydroExprException($"variavel '{v}' nao existe nesta peca");
        }

        double r = _root.Eval(vars);
        if (double.IsNaN(r) || double.IsInfinity(r))
            throw new HydroExprException("resultado invalido");
        return r;
    }

    public double Evaluate(IReadOnlyDictionary<string, double> vars) =>
        Evaluate(name => vars.TryGetValue(name, out double v) ? v : null);

    /// <summary>Le um numero com virgula ou ponto decimal.</summary>
    public static bool TryNumber(string text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ------------------------------------------------------------------

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public readonly HashSet<string> Vars = new(StringComparer.Ordinal);

        public Parser(string s) => _s = s;

        public Node ParseAll()
        {
            Node n = Compare();
            Skip();
            if (_i < _s.Length)
                throw Error($"'{_s[_i]}' inesperado");
            return n;
        }

        private HydroExprException Error(string what) => new($"{what} na posicao {_i + 1} de \"{_s}\"");

        private void Skip()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                _i++;
        }

        private bool Eat(string op)
        {
            Skip();
            if (string.CompareOrdinal(_s, _i, op, 0, op.Length) != 0)
                return false;
            _i += op.Length;
            return true;
        }

        private Node Compare()
        {
            Node a = Sum();
            foreach (string op in new[] { ">=", "<=", "==", "!=", "<>", ">", "<", "=" })
            {
                if (Eat(op))
                    return new Binary { Op = op, A = a, B = Sum() };
            }

            return a;
        }

        private Node Sum()
        {
            Node a = Product();
            while (true)
            {
                if (Eat("+"))
                    a = new Binary { Op = "+", A = a, B = Product() };
                else if (Eat("-") || Eat("−"))
                    a = new Binary { Op = "-", A = a, B = Product() };
                else
                    return a;
            }
        }

        private Node Product()
        {
            Node a = Power();
            while (true)
            {
                if (Eat("*") || Eat("×"))
                    a = new Binary { Op = "*", A = a, B = Power() };
                else if (Eat("/"))
                    a = new Binary { Op = "/", A = a, B = Power() };
                else
                    return a;
            }
        }

        private Node Power()
        {
            Node a = Signed();
            if (Eat("^"))
                return new Binary { Op = "^", A = a, B = Power() };
            return a;
        }

        private Node Signed()
        {
            if (Eat("-") || Eat("−"))
                return new Unary { Op = '-', Arg = Signed() };
            if (Eat("+"))
                return Signed();
            return Atom();
        }

        private Node Atom()
        {
            Skip();
            if (_i >= _s.Length)
                throw Error("fim inesperado");
            char c = _s[_i];
            if (c == '(')
            {
                _i++;
                Node n = Compare();
                if (!Eat(")"))
                    throw Error("falta ')'");
                return n;
            }

            if (char.IsDigit(c) || c == '.' || c == ',')
                return Number();

            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int start = _i;
                if (c == '$')
                    _i++;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
                    _i++;
                string name = Normalize(_s[start.._i].TrimStart('$'));
                if (name.Length == 0)
                    throw Error("nome vazio");
                if (Eat("("))
                {
                    Call call = new() { Name = name };
                    if (!Eat(")"))
                    {
                        do
                        {
                            call.Args.Add(Compare());
                        }
                        while (Eat(";"));
                        if (!Eat(")"))
                            throw Error($"falta ')' em {name}( (argumentos separados por ';')");
                    }

                    return call;
                }

                if (name == "pi")
                    return new Num { Value = Math.PI };
                Vars.Add(name);
                return new Var { Name = name };
            }

            throw Error($"'{c}' inesperado");
        }

        private Node Number()
        {
            int start = _i;
            bool sep = false;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsDigit(c))
                {
                    _i++;
                }
                else if ((c == '.' || c == ',') && !sep && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1]))
                {
                    sep = true;
                    _i++;
                }
                else
                {
                    break;
                }
            }

            string text = _s[start.._i];
            if (!TryNumber(text, out double v))
                throw Error($"numero '{text}' invalido");
            return new Num { Value = v };
        }
    }

    /// <summary>Nome de variavel normalizado: minusculas, sem acento.</summary>
    public static string Normalize(string name)
    {
        string d = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(d.Length);
        foreach (char ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString();
    }
}

/// <summary>Resultado da avaliacao das regras de uma peca.</summary>
public sealed class HydroRuleResult
{
    /// <summary>Valor calculado por alvo (minusculas), nas unidades das variaveis (m).</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Erro por alvo, com a mensagem para o usuario.</summary>
    public Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Avalia um conjunto de regras (alvo = expressao) sobre as variaveis da peca. Uma regra pode usar
/// o resultado de outra (comprimento = largura): a ordem sai das dependencias; ciclo vira erro.
/// </summary>
public static class HydroRuleEngine
{
    public static HydroRuleResult Evaluate(IReadOnlyDictionary<string, string> rules, IReadOnlyDictionary<string, double> vars)
    {
        HydroRuleResult result = new();
        Dictionary<string, HydroExpr> parsed = new(StringComparer.Ordinal);
        foreach ((string target, string text) in rules)
        {
            string key = HydroExpr.Normalize(target);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (HydroExpr.TryParse(text, out HydroExpr? e, out string error))
                parsed[key] = e!;
            else
                result.Errors[key] = error;
        }

        Dictionary<string, double> current = new(StringComparer.Ordinal);
        foreach ((string k, double v) in vars)
            current[HydroExpr.Normalize(k)] = v;

        // 0 = nao visitado, 1 = em andamento, 2 = feito.
        Dictionary<string, int> state = new(StringComparer.Ordinal);

        void Visit(string key, Stack<string> path)
        {
            if (state.TryGetValue(key, out int s))
            {
                if (s == 1)
                    throw new HydroExprException("regras em ciclo: " + string.Join(" > ", path.Reverse().Append(key)));
                return;
            }

            state[key] = 1;
            path.Push(key);
            HydroExpr e = parsed[key];
            foreach (string dep in e.Variables)
            {
                if (dep != key && parsed.ContainsKey(dep) && !result.Errors.ContainsKey(dep))
                    Visit(dep, path);
            }

            foreach (string dep in e.Variables)
            {
                if (dep != key && result.Errors.ContainsKey(dep))
                    throw new HydroExprException($"depende de '{dep}', que tem erro");
            }

            double v = e.Evaluate(name => current.TryGetValue(name, out double x) ? x : null);
            current[key] = v;
            result.Values[key] = v;
            path.Pop();
            state[key] = 2;
        }

        foreach (string key in parsed.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (state.ContainsKey(key) && state[key] == 2)
                continue;
            try
            {
                Visit(key, new Stack<string>());
            }
            catch (HydroExprException ex)
            {
                // Marca o alvo e os que ficaram em andamento no caminho.
                foreach (string k in state.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList())
                {
                    result.Errors[k] = ex.Message;
                    state[k] = 2;
                }

                if (!result.Values.ContainsKey(key))
                    result.Errors.TryAdd(key, ex.Message);
            }
        }

        return result;
    }

    /// <summary>
    /// Texto digitado num campo de medida: vazio = nulo, "+0,10" / "-0,30" relativo ao atual,
    /// numero = absoluto, "=expr" = expressao avaliada agora (sem virar regra).
    /// </summary>
    public static double? EditValue(string text, double current, Func<string, double?>? vars = null)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0)
            return null;
        if (t[0] == '+' || t[0] == '-' || t[0] == '−')
        {
            string rest = t[1..].Trim();
            if (!HydroExpr.TryNumber(rest, out double d))
                throw new HydroExprException($"'{t}': depois do sinal vai um numero (ex. +0,10)");
            return t[0] == '+' ? current + d : current - d;
        }

        if (HydroExpr.TryNumber(t, out double v))
            return v;
        return HydroExpr.Parse(t).Evaluate(vars ?? (_ => null));
    }

    /// <summary>
    /// Ajusta o valor a uma lista fechada do catalogo: o menor valor da lista que nao fica abaixo
    /// (a peca nunca sai menor que a regra pede); acima do maior, devolve o maior e avisa.
    /// </summary>
    public static double SnapUp(double value, IReadOnlyList<double> list, out bool overflow)
    {
        overflow = false;
        if (list.Count == 0)
            return value;
        double best = double.NaN;
        foreach (double x in list)
        {
            if (x >= value - 1e-9 && (double.IsNaN(best) || x < best))
                best = x;
        }

        if (double.IsNaN(best))
        {
            overflow = true;
            return list.Max();
        }

        return best;
    }
}
