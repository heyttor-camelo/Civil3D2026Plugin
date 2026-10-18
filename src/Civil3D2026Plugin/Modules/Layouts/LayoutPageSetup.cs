using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Page setup (configuracao de pagina) nomeado do desenho.</summary>
internal sealed record PageSetupItem(string Nome, string Dispositivo, string Papel, string Estilo, string Origem);

/// <summary>
/// Page setup em lote: aplica page setups nomeados ou uma configuracao direta (plotter,
/// papel, CTB) em varios layouts; importa page setups de outro DWG/DWT; salva o do layout.
/// </summary>
internal static class LayoutPageSetup
{
    // ------------------------------------------------------------------
    // Leitura
    // ------------------------------------------------------------------

    /// <summary>Page setups de papel (nao Model) guardados no desenho.</summary>
    public static List<PageSetupItem> Ler(Database db)
    {
        var lista = new List<PageSetupItem>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                if (tr.GetObject(e.Value, OpenMode.ForRead) is not PlotSettings ps || ps.ModelType)
                    continue;
                lista.Add(new PageSetupItem(e.Key, ps.PlotConfigurationName, ps.CanonicalMediaName, ps.CurrentStyleSheet, "desenho"));
            }
            tr.Commit();
        }
        return lista.OrderBy(p => p.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Plotters/PC3 disponiveis.</summary>
    public static List<string> Dispositivos()
    {
        try
        {
            return Lista(PlotSettingsValidator.Current.GetPlotDeviceList());
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>Estilos de plotagem (CTB/STB) disponiveis.</summary>
    public static List<string> Estilos()
    {
        try
        {
            return Lista(PlotSettingsValidator.Current.GetPlotStyleSheetList());
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>Papeis do dispositivo: nome canonico e nome mostrado.</summary>
    public static List<(string Canonico, string Nome)> Papeis(string dispositivo)
    {
        var lista = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(dispositivo))
            return lista;
        try
        {
            PlotSettingsValidator psv = PlotSettingsValidator.Current;
            using var ps = new PlotSettings(false);
            psv.SetPlotConfigurationName(ps, dispositivo, null);
            psv.RefreshLists(ps);
            foreach (string? canon in psv.GetCanonicalMediaNameList(ps))
            {
                if (string.IsNullOrEmpty(canon))
                    continue;
                string nome = canon;
                try
                {
                    nome = psv.GetLocaleMediaName(ps, canon) ?? canon;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                }
                lista.Add((canon, nome));
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        return lista;
    }

    private static List<string> Lista(StringCollection col)
    {
        var l = new List<string>();
        foreach (string? s in col)
            if (!string.IsNullOrWhiteSpace(s))
                l.Add(s);
        return l;
    }

    // ------------------------------------------------------------------
    // Aplicar
    // ------------------------------------------------------------------

    /// <summary>Copia o page setup nomeado para cada layout.</summary>
    public static OpResult AplicarNomeado(Document doc, string pageSetup, IReadOnlyList<string> layouts)
    {
        int ok = 0, falhas = 0;
        var erros = new List<string>();
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var psDic = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            if (!psDic.Contains(pageSetup))
                return new OpResult(0, layouts.Count, $"Page setup \"{pageSetup}\" nao existe no desenho.");
            var ps = (PlotSettings)tr.GetObject(psDic.GetAt(pageSetup), OpenMode.ForRead);

            var lDic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (string nome in layouts)
            {
                try
                {
                    if (!lDic.Contains(nome))
                        continue;
                    var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(lDic.GetAt(nome), OpenMode.ForWrite);
                    layout.CopyFrom(ps);
                    ok++;
                }
                catch (System.Exception ex)
                {
                    erros.Add($"{nome}: {ex.Message}");
                    falhas++;
                }
            }
            tr.Commit();
        }
        return new OpResult(ok, falhas, $"Page setup \"{pageSetup}\" aplicado em {ok} layout(s)" +
                                        (falhas > 0 ? $"; {falhas} falha(s): {string.Join("; ", erros.Take(3))}" : "."));
    }

    /// <summary>
    /// Configuracao direta: troca plotter/papel/CTB (os vazios ficam como estao) e,
    /// se pedido, area de plotagem Layout em escala 1:1 centralizada.
    /// </summary>
    public static OpResult AplicarDireto(Document doc, IReadOnlyList<string> layouts,
        string? dispositivo, string? papelCanonico, string? estilo, bool layout11)
    {
        int ok = 0, falhas = 0;
        var erros = new List<string>();
        PlotSettingsValidator psv = PlotSettingsValidator.Current;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var lDic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (string nome in layouts)
            {
                if (!lDic.Contains(nome))
                    continue;
                try
                {
                    var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(lDic.GetAt(nome), OpenMode.ForWrite);
                    using var ps = new PlotSettings(false);
                    ps.CopyFrom(layout);

                    if (!string.IsNullOrWhiteSpace(dispositivo) || !string.IsNullOrWhiteSpace(papelCanonico))
                    {
                        string dev = string.IsNullOrWhiteSpace(dispositivo) ? layout.PlotConfigurationName : dispositivo!;
                        string? media = string.IsNullOrWhiteSpace(papelCanonico) ? null : papelCanonico;
                        psv.SetPlotConfigurationName(ps, dev, media);
                        psv.RefreshLists(ps);
                    }
                    if (!string.IsNullOrWhiteSpace(estilo))
                        psv.SetCurrentStyleSheet(ps, estilo);
                    if (layout11)
                    {
                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                        psv.SetUseStandardScale(ps, true);
                        psv.SetStdScaleType(ps, StdScaleType.StdScale1To1);
                        psv.SetPlotCentered(ps, false);
                        psv.SetPlotOrigin(ps, new Autodesk.AutoCAD.Geometry.Point2d(0, 0));
                    }

                    layout.CopyFrom(ps);
                    ok++;
                }
                catch (System.Exception ex)
                {
                    erros.Add($"{nome}: {ex.Message}");
                    falhas++;
                }
            }
            tr.Commit();
        }
        return new OpResult(ok, falhas, $"Configuracao aplicada em {ok} layout(s)" +
                                        (falhas > 0 ? $"; {falhas} falha(s): {string.Join("; ", erros.Take(3))}" : "."));
    }

    // ------------------------------------------------------------------
    // Importar / salvar
    // ------------------------------------------------------------------

    /// <summary>Importa os page setups de papel de outro DWG/DWT (os de mesmo nome sao substituidos).</summary>
    public static OpResult Importar(Document doc, string arquivo)
    {
        int novos = 0, trocados = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;

        using var origem = new Database(false, true);
        origem.ReadDwgFile(arquivo, System.IO.FileShare.Read, true, "");
        origem.CloseInput(true);

        using Transaction trO = origem.TransactionManager.StartTransaction();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dicO = (DBDictionary)trO.GetObject(origem.PlotSettingsDictionaryId, OpenMode.ForRead);
        var dic = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);

        foreach (DBDictionaryEntry e in dicO)
        {
            if (trO.GetObject(e.Value, OpenMode.ForRead) is not PlotSettings src || src.ModelType)
                continue;

            if (dic.Contains(e.Key))
            {
                var atual = (PlotSettings)tr.GetObject(dic.GetAt(e.Key), OpenMode.ForWrite);
                atual.CopyFrom(src);
                trocados++;
            }
            else
            {
                var ps = new PlotSettings(false);
                ps.CopyFrom(src);
                ps.PlotSettingsName = e.Key;
                ps.AddToPlotSettingsDictionary(db);
                tr.AddNewlyCreatedDBObject(ps, true);
                novos++;
            }
        }

        tr.Commit();
        trO.Commit();
        return new OpResult(novos + trocados, 0,
            novos + trocados > 0
                ? $"{novos} page setup(s) importado(s), {trocados} substituido(s) de {System.IO.Path.GetFileName(arquivo)}."
                : $"Nenhum page setup de papel em {System.IO.Path.GetFileName(arquivo)}.");
    }

    /// <summary>Guarda a configuracao atual do layout como page setup nomeado.</summary>
    public static OpResult SalvarDoLayout(Document doc, string layout, string nome)
    {
        nome = nome.Trim();
        if (nome.Length == 0)
            return new OpResult(0, 1, "Informe o nome do page setup.");

        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var lDic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (!lDic.Contains(layout))
            return new OpResult(0, 1, $"Layout \"{layout}\" nao encontrado.");
        var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(lDic.GetAt(layout), OpenMode.ForRead);

        var dic = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
        if (dic.Contains(nome))
        {
            var atual = (PlotSettings)tr.GetObject(dic.GetAt(nome), OpenMode.ForWrite);
            atual.CopyFrom(lay);
            atual.PlotSettingsName = nome;
        }
        else
        {
            var ps = new PlotSettings(false);
            ps.CopyFrom(lay);
            ps.PlotSettingsName = nome;
            ps.AddToPlotSettingsDictionary(db);
            tr.AddNewlyCreatedDBObject(ps, true);
        }
        tr.Commit();
        return new OpResult(1, 0, $"Page setup \"{nome}\" salvo a partir de \"{layout}\".");
    }
}
