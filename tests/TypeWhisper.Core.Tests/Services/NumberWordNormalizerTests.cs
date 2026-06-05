using TypeWhisper.Core.Services.NumberNormalization;

namespace TypeWhisper.Core.Tests.Services;

public class NumberWordNormalizerTests
{
    [Fact]
    public void Normalize_EnglishSimpleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("I have two questions", "en");

        Assert.Equal("I have 2 questions", result);
    }

    [Fact]
    public void Normalize_GermanSimpleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("ich habe zwei Fragen", "de");

        Assert.Equal("ich habe 2 Fragen", result);
    }

    [Fact]
    public void Normalize_EnglishCompoundNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("twenty three files", "en-US");

        Assert.Equal("23 files", result);
    }

    [Fact]
    public void Normalize_GermanCompoundNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("dreiundzwanzig Dateien", "de_DE");

        Assert.Equal("23 Dateien", result);
    }

    [Fact]
    public void Normalize_EnglishScaleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("one thousand two hundred thirty four", "en");

        Assert.Equal("1234", result);
    }

    [Fact]
    public void Normalize_GermanScaleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("eintausendzweihundertvierunddreißig", "de");

        Assert.Equal("1234", result);
    }

    [Fact]
    public void Normalize_EnglishNegativeDecimal_ReturnsDotDecimal()
    {
        var result = NumberWordNormalizer.Normalize("minus two point five", "en");

        Assert.Equal("-2.5", result);
    }

    [Fact]
    public void Normalize_EnglishAndSeparator_DoesNotMergeIndependentNumbers()
    {
        Assert.Equal("2 and 3", NumberWordNormalizer.Normalize("two and three", "en"));
        Assert.Equal(
            "between 2 and 3 minutes",
            NumberWordNormalizer.Normalize("between two and three minutes", "en"));
    }

    [Fact]
    public void Normalize_EnglishHundredAndScale_ConsumesAnd()
    {
        Assert.Equal("123", NumberWordNormalizer.Normalize("one hundred and twenty three", "en"));
        Assert.Equal("1005", NumberWordNormalizer.Normalize("one thousand and five", "en"));
    }

    [Fact]
    public void Normalize_GermanNegativeDecimal_ReturnsCommaDecimal()
    {
        var result = NumberWordNormalizer.Normalize("minus zwei komma fünf", "de");

        Assert.Equal("-2,5", result);
    }

    [Fact]
    public void Normalize_FrenchNumbers_ReturnDigits()
    {
        Assert.Equal("J'ai 2 questions", NumberWordNormalizer.Normalize("J'ai deux questions", "fr"));
        Assert.Equal("23 fichiers", NumberWordNormalizer.Normalize("vingt trois fichiers", "fr"));
        Assert.Equal("1234", NumberWordNormalizer.Normalize("mille deux cent trente quatre", "fr"));
        Assert.Equal("-2,5", NumberWordNormalizer.Normalize("moins deux virgule cinq", "fr"));
    }

    [Fact]
    public void Normalize_FrenchArticleOne_IsPreservedOutsideClearNumberConstructs()
    {
        Assert.Equal("j'ai un probleme", NumberWordNormalizer.Normalize("j'ai un probleme", "fr"));
        Assert.Equal("1000000 de lignes", NumberWordNormalizer.Normalize("un million de lignes", "fr"));
    }

    [Fact]
    public void Normalize_SpanishNumbers_ReturnDigits()
    {
        Assert.Equal("tengo 2 preguntas", NumberWordNormalizer.Normalize("tengo dos preguntas", "es"));
        Assert.Equal("23 archivos", NumberWordNormalizer.Normalize("veintitrés archivos", "es"));
        Assert.Equal("23 archivos", NumberWordNormalizer.Normalize("veinte y tres archivos", "es"));
        Assert.Equal("1234", NumberWordNormalizer.Normalize("mil doscientos treinta y cuatro", "es"));
        Assert.Equal("-2,5", NumberWordNormalizer.Normalize("menos dos coma cinco", "es"));
    }

    [Fact]
    public void Normalize_SpanishArticleOne_IsPreservedOutsideClearNumberConstructs()
    {
        Assert.Equal("tengo un problema", NumberWordNormalizer.Normalize("tengo un problema", "es"));
        Assert.Equal("1000000 de filas", NumberWordNormalizer.Normalize("un millón de filas", "es"));
    }

    [Fact]
    public void Normalize_ChineseHanNumbers_ReturnDigits()
    {
        Assert.Equal("我有23个文件", NumberWordNormalizer.Normalize("我有二十三个文件", "zh"));
        Assert.Equal("1234", NumberWordNormalizer.Normalize("一千二百三十四", "zh"));
        Assert.Equal("-2.5", NumberWordNormalizer.Normalize("负二点五", "zh"));
    }

    [Fact]
    public void Normalize_JapaneseHanNumbers_ReturnDigits()
    {
        Assert.Equal("23個のファイル", NumberWordNormalizer.Normalize("二十三個のファイル", "ja"));
        Assert.Equal("1234", NumberWordNormalizer.Normalize("千二百三十四", "ja"));
        Assert.Equal("-2.5", NumberWordNormalizer.Normalize("負二点五", "ja"));
    }

    [Fact]
    public void Normalize_JapaneseSingleKanjiInWords_IsPreserved()
    {
        var result = NumberWordNormalizer.Normalize("一緒に行く", "ja");

        Assert.Equal("一緒に行く", result);
    }

    [Fact]
    public void Normalize_UnsupportedLanguage_IsNoOp()
    {
        var result = NumberWordNormalizer.Normalize("twenty three", "it");

        Assert.Equal("twenty three", result);
    }

    [Fact]
    public void Normalize_AlreadyDigitText_IsNoOp()
    {
        var result = NumberWordNormalizer.Normalize("I have 23 files", "en");

        Assert.Equal("I have 23 files", result);
    }

    [Fact]
    public void Normalize_GermanArticleOne_IsPreservedOutsideClearNumberConstructs()
    {
        Assert.Equal("ich habe ein Problem", NumberWordNormalizer.Normalize("ich habe ein Problem", "de"));
        Assert.Equal("100 Euro", NumberWordNormalizer.Normalize("ein hundert Euro", "de"));
    }
}
