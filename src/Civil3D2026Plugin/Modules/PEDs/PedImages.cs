using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

using WMI = System.Windows.Media.Imaging;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Extrai as imagens "dentro da celula" da planilha para uma pasta (PEDs_img),
/// reduzidas e em JPEG: o xlsx original tem ~2 MB por imagem em PNG.
/// O DWG guarda so o nome do arquivo.
/// </summary>
internal static class PedImages
{
    public const string FolderName = "PEDs_img";

    /// <summary>Maior lado da imagem gravada (px).</summary>
    private const int MaxSide = 1600;

    /// <summary>Nome amigavel por coluna conhecida da ficha (foto do local, mapa de localizacao).</summary>
    public static string Suffix(string letter) => letter.ToUpperInvariant() switch
    {
        PedFicha.ColFoto => "foto",
        PedFicha.ColMapa => "mapa",
        _ => letter.ToUpperInvariant()
    };

    /// <summary>
    /// Grava as imagens de todos os PEDs. Retorna, por codigo, letra -> nome do arquivo.
    /// Erros por imagem nao interrompem: vao para <paramref name="errors"/>.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ExtractAll(
        string xlsxPath, IEnumerable<PedRecord> records, string folder, List<string> errors, Action<int>? progress = null)
    {
        Directory.CreateDirectory(folder);
        Dictionary<string, Dictionary<string, string>> result = new(StringComparer.OrdinalIgnoreCase);

        using FileStream fs = new(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive zip = new(fs, ZipArchiveMode.Read);

        int n = 0;
        foreach (PedRecord rec in records)
        {
            Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string letter, string entry) in rec.ImageEntries)
            {
                string fileName = SafeFileName(rec.Code) + "_" + Suffix(letter) + ".jpg";
                try
                {
                    byte[]? bytes = PedXlsxReader.ReadEntry(zip, entry);
                    if (bytes is null)
                    {
                        errors.Add($"{rec.Code}: imagem {entry} nao encontrada no xlsx.");
                        continue;
                    }

                    SaveJpeg(bytes, Path.Combine(folder, fileName));
                    files[letter] = fileName;
                }
                catch (Exception ex)
                {
                    errors.Add($"{rec.Code} ({letter}): {ex.Message}");
                }
            }

            result[rec.Code] = files;
            progress?.Invoke(++n);
        }

        return result;
    }

    private static void SaveJpeg(byte[] bytes, string path)
    {
        using MemoryStream input = new(bytes);
        WMI.BitmapDecoder decoder = WMI.BitmapDecoder.Create(
            input, WMI.BitmapCreateOptions.IgnoreColorProfile | WMI.BitmapCreateOptions.PreservePixelFormat, WMI.BitmapCacheOption.OnLoad);
        WMI.BitmapSource source = decoder.Frames[0];

        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > MaxSide)
        {
            double s = (double)MaxSide / longest;
            source = new WMI.TransformedBitmap(source, new System.Windows.Media.ScaleTransform(s, s));
        }

        // JPEG nao tem alfa: converte para Bgr24 (os prints do Google Earth sao opacos).
        if (source.Format != System.Windows.Media.PixelFormats.Bgr24)
            source = new WMI.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0);

        WMI.JpegBitmapEncoder encoder = new() { QualityLevel = 85 };
        encoder.Frames.Add(WMI.BitmapFrame.Create(source));

        string tmp = path + ".tmp";
        using (FileStream output = new(tmp, FileMode.Create, FileAccess.Write))
            encoder.Save(output);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Carrega a imagem sem travar o arquivo (para a ficha).</summary>
    public static WMI.BitmapImage? Load(string path, int decodeWidth = 0)
    {
        try
        {
            WMI.BitmapImage img = new();
            img.BeginInit();
            img.CacheOption = WMI.BitmapCacheOption.OnLoad;
            img.CreateOptions = WMI.BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path, UriKind.Absolute);
            if (decodeWidth > 0)
                img.DecodePixelWidth = decodeWidth;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    public static string SafeFileName(string code)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            code = code.Replace(c, '_');
        return code.Trim();
    }
}
