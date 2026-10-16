namespace Civil3D2026Plugin.Infrastructure;

/// <summary>
/// Fonte unica para os nomes publicos dos comandos.
///
/// Para renomear um comando no futuro, altere SOMENTE a constante correspondente
/// neste arquivo. Os CommandMethod e o catalogo de comandos usam estas constantes.
/// </summary>
public static class CommandNames
{
    public static class Core
    {
        public const string Help = "C3DHELP";
        public const string Ribbon = "C3DRIBBON";
        public const string RibbonReset = "C3DRIBBONRESET";
        public const string Modulos = "C3DMODULOS";
    }

    public static class Drenagem
    {
        public const string Excel = "DRENEXCEL";
        public const string ExcelPreview = "DRENEXCELPREVIEW";
        public const string ExcelHelp = "DRENEXCELHELP";

        public const string Num = "DRENNUM";
        public const string NumPreview = "DRENNUMPREVIEW";
        public const string NumHelp = "DRENNUMHELP";
    }

    public static class Qto
    {
        public const string Estudo = "QTOESTUDO";
        public const string GeoTeste = "QTOGEOTESTE";
        public const string Help = "QTOHELP";
        public const string Teste = "TESTEQTO";
    }

    public static class Corridor
    {
        public const string Split = "CORRSPLIT";
        public const string MfRebaixo = "MFREBAIXO";
        public const string MfRebaixoCfg = "MFREBAIXOCFG";
        public const string MfRebaixoHelp = "MFREBAIXOHELP";
        public const string MfRebaixoPainel = "MFREBAIXOPAINEL";

        public const string SolidArray = "C3DSOLIDARRAY";
        public const string SolidCfg = "C3DSOLIDCFG";
        public const string SolidAtualizar = "C3DSOLIDATUALIZAR";
        public const string SolidEditar = "C3DSOLIDEDITAR";
        public const string SolidItem = "C3DSOLIDITEM";
        public const string SolidHelp = "C3DSOLIDHELP";

        public const string SolidLigar = "C3DSOLIDLIGAR";
        public const string SolidLigarEditar = "C3DSOLIDLIGAREDITAR";
        public const string SolidLigarAtualizar = "C3DSOLIDLIGARATUALIZAR";
        public const string SolidLigarHelp = "C3DSOLIDLIGARHELP";
        public const string SolidLigarTooltip = "C3DSOLIDLIGARTOOLTIP";

        public const string OcultarPlanta = "C3DOCULTARPLANTA";
        public const string PlantaAtualizar = "C3DPLANTAATUALIZAR";

        public const string Passagem = "PASSAGEM";
        public const string PassagemCfg = "PASSAGEMCFG";
        public const string PassagemEditar = "PASSAGEMEDITAR";
        public const string PassagemGrupo = "PASSAGEMGRUPO";
        public const string PassagemAtualizar = "PASSAGEMATUALIZAR";
        public const string PassagemHelp = "PASSAGEMHELP";
    }

    public static class Quadros
    {
        public const string Ruas = "C3DQUADRORUAS";
    }

    public static class Viario
    {
        public const string Rede = "C3DVIARIO";
        public const string Eixo = "C3DVIARIOEIXO";
        public const string Raio = "C3DVIARIORAIO";
        public const string Modelos = "C3DVIARIOMODELOS";
        public const string Alargar = "C3DVIARIOALARGAR";
        public const string Gota = "C3DVIARIOGOTA";
        public const string Conversao = "C3DVIARIOCONVERSAO";
        public const string Abertura = "C3DVIARIOABERTURA";
        public const string Livre = "C3DVIARIOLIVRE";
        public const string ElemRemover = "C3DVIARIOELEMREMOVER";
        public const string Civil = "C3DVIARIOCIVIL";
        public const string Teste = "C3DVIARIOTESTE";
        public const string Ilha = "C3DVIARIOILHA";
        public const string Diag = "C3DVIARIODIAG";
        public const string Painel = "C3DVIARIOPAINEL";
        public const string Sinal = "C3DVIARIOSINAL";
        public const string Cotar = "C3DVIARIOCOTAR";
        public const string Hachura = "C3DVIARIOHACHURA";
    }

    public static class FeatureLines
    {
        public const string ZSet = "FLZSET";
        public const string Descarregar = "FLDESCARREGAR";
        public const string SetPv = "FLSETPV";
        public const string RenomearCorte = "FLRENOMEARCORTE";
    }

    public static class Superficies
    {
        public const string ColorAuto = "SURFCOLORAUTO";
        public const string ReadScheme = "SURFREADSCHEME";
        public const string SlopeFilter = "SFSLOPEFILTER";
        public const string TrimLines = "TRIMSURFLINES";
        public const string Cortar1Mm = "CORTAR1MM";
    }

    public static class Geometria
    {
        public const string PointNormalDebug = "PTNORMALDBG";
    }

    /// <summary>Utilitarios convertidos das LISPs: mantem o nome antigo do comando.</summary>
    public static class Utilitarios
    {
        public const string AllToRgb = "ALLTORGB";
        public const string Tlen = "TLEN";
        public const string JobeCount = "JOBECOUNT";
        public const string SmartHatch = "SMARTHATCH";
        public const string LsLayouts = "LSLAYOUTS";
        public const string Moff = "MOFF";
        public const string MoffSet = "MOFFSET";
        public const string SeparaCurvas = "SEPARA_CURVAS_RAIO";
        public const string Kb = "KB";
        public const string Pp = "PP";
    }

    public static class Layouts
    {
        public const string Painel = "C3DLAYOUT";
        public const string Viewports = "C3DLAYOUTVP";
        public const string LayerState = "C3DLAYOUTLS";
        public const string Carimbo = "C3DLAYOUTCARIMBO";
        public const string Numerar = "NP";
    }

    public static class Peds
    {
        public const string Importar = "PEDIMPORTAR";
        public const string Atualizar = "PEDATUALIZAR";
        public const string Locar = "PEDLOCAR";
        public const string Ficha = "PEDFICHA";
        public const string Vista = "PEDVISTA";
        public const string Modelo = "PEDMODELO";
    }

    public static class LispTools
    {
        public const string Painel = "C3DLISP";
        public const string Lista = "C3DLISPLIST";
        public const string Carregar = "C3DLISPCARREGAR";
    }

    public static class Diagnostico
    {
        public const string TesteDll = "TESTEDLL";
    }
}
