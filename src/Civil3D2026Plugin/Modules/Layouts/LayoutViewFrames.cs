using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Um view frame ou match line do Civil 3D, para a lista do painel.</summary>
internal sealed record ViewFrameItem(
    ObjectId Id,
    string Tipo,          // "Frame" ou "Match line"
    string Grupo,
    string Nome,
    string Eixo,
    double EstacaInicio,  // match line: estaca da linha (fim = NaN)
    double EstacaFim,
    string Folha,         // ViewFrame.Sheet (arquivo + layout), "" se nao tem
    string Layout,        // layout deste desenho que corresponde a Folha, "" se nenhum
    bool RotuloVisivel,
    Extents3d? Extensao);

/// <summary>
/// View frames do Civil 3D. A API .NET so le os frames (estacas, eixo, grupo, folha) e
/// controla os rotulos; criar view frames ou folhas nao e exposto (so pelo assistente
/// do Civil). Os membros sao lidos por reflexao para nao depender da versao exata da API.
/// </summary>
internal static class LayoutViewFrames
{
    public static List<ViewFrameItem> Ler(Database db, IReadOnlyCollection<string> layoutsDoDesenho)
    {
        var lista = new List<ViewFrameItem>();
        CivilDocument? civ;
        try
        {
            civ = CivilDocument.GetCivilDocument(db);
        }
        catch (System.Exception)
        {
            return lista;
        }
        if (civ is null)
            return lista;

        ObjectIdCollection? grupos = Chamar(civ, "GetViewFrameGroupIds") as ObjectIdCollection;
        if (grupos is null || grupos.Count == 0)
            return lista;

        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId gid in grupos)
        {
            DBObject grupo = tr.GetObject(gid, OpenMode.ForRead);
            string nomeGrupo = Texto(grupo, "Name");

            if (Chamar(grupo, "GetViewFrameIds") is ObjectIdCollection frames)
            {
                foreach (ObjectId fid in frames)
                {
                    DBObject f = tr.GetObject(fid, OpenMode.ForRead);
                    string folha = Texto(f, "Sheet");
                    lista.Add(new ViewFrameItem(
                        fid, "Frame", nomeGrupo, Texto(f, "Name"), NomeEixo(tr, f),
                        Numero(f, "StartStation"), Numero(f, "EndStation"),
                        folha, LayoutDaFolha(folha, layoutsDoDesenho),
                        Booleano(f, "IsLabelVisible"), Extensao(f)));
                }
            }

            if (Chamar(grupo, "GetMatchLineIds") is ObjectIdCollection linhas)
            {
                foreach (ObjectId mid in linhas)
                {
                    DBObject m = tr.GetObject(mid, OpenMode.ForRead);
                    bool esq = Booleano(m, "IsLeftLabelVisible"), dir = Booleano(m, "IsRightLabelVisible");
                    lista.Add(new ViewFrameItem(
                        mid, "Match line", nomeGrupo, "Match line " + Texto(m, "Number"), NomeEixo(tr, m),
                        Numero(m, "Station"), double.NaN, "", "", esq || dir, Extensao(m)));
                }
            }
        }
        tr.Commit();
        return lista;
    }

    /// <summary>Liga/desliga os rotulos dos frames (IsLabelVisible) e match lines (esquerdo e direito).</summary>
    public static OpResult Rotulos(Document doc, IEnumerable<ObjectId> ids, bool visivel)
    {
        int ok = 0, falhas = 0;
        using DocumentLock _ = doc.LockDocument();
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in ids)
            {
                try
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForWrite);
                    bool algum = Definir(o, "IsLabelVisible", visivel)
                               | Definir(o, "IsLeftLabelVisible", visivel)
                               | Definir(o, "IsRightLabelVisible", visivel);
                    if (algum) ok++; else falhas++;
                }
                catch (System.Exception)
                {
                    falhas++;
                }
            }
            tr.Commit();
        }
        return new OpResult(ok, falhas, $"Rotulos {(visivel ? "ligados" : "desligados")} em {ok} objeto(s)" + (falhas > 0 ? $"; {falhas} sem rotulo editavel." : "."));
    }

    public static string Estaca(double v)
    {
        if (double.IsNaN(v))
            return "";
        // Estaqueamento de 20 m: E + fracao (ex.: 12+5,30), como no QTO. Arredonda ao centimetro
        // antes de separar a estaca, senao 239,99999 vira "11+20,00".
        v = Math.Round(v, 2);
        int est = (int)Math.Floor(v / 20.0 + 1e-9);
        double resto = v - est * 20.0;
        return $"{est}+{resto.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"))}";
    }

    // ------------------------------------------------------------------
    // Reflexao
    // ------------------------------------------------------------------

    private static object? Chamar(object alvo, string metodo)
    {
        try
        {
            return alvo.GetType().GetMethod(metodo, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.Invoke(alvo, null);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static object? Valor(object alvo, string prop)
    {
        try
        {
            return alvo.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(alvo);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static bool Definir(object alvo, string prop, object valor)
    {
        PropertyInfo? p = alvo.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite)
            return false;
        p.SetValue(alvo, valor);
        return true;
    }

    private static string Texto(object o, string prop) => Convert.ToString(Valor(o, prop), CultureInfo.InvariantCulture) ?? "";

    private static double Numero(object o, string prop) =>
        Valor(o, prop) is object v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN;

    private static bool Booleano(object o, string prop) => Valor(o, prop) is bool b && b;

    private static string NomeEixo(Transaction tr, DBObject o)
    {
        if (Valor(o, "AlignmentId") is not ObjectId aid || aid.IsNull || aid.IsErased)
            return "";
        return Texto(tr.GetObject(aid, OpenMode.ForRead), "Name");
    }

    private static Extents3d? Extensao(DBObject o)
    {
        try
        {
            return o is Entity e ? e.GeometricExtents : null;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    /// <summary>A propriedade Sheet traz arquivo e layout; procura qual layout deste desenho ela cita.</summary>
    private static string LayoutDaFolha(string folha, IReadOnlyCollection<string> layouts)
    {
        if (folha.Length == 0)
            return "";
        return layouts
            .Where(l => folha.EndsWith(l, StringComparison.OrdinalIgnoreCase) || folha.Contains("|" + l, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Length)
            .FirstOrDefault() ?? "";
    }
}
