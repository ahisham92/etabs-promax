using EtabsProMax.E2k;

namespace EtabsProMax.Model;

/// <summary>The model ETABS makes for File > New Model with its defaults, as ETABS 23.2 exported it. It carries the
/// sections of the text file that have nothing to do with the building's shape but that ETABS expects to find.</summary>
public static class Seed
{
    public static StructuralModel Etabs23()
    {
        using var stream = typeof(Seed).Assembly.GetManifestResourceStream("etabs23.e2k")
                           ?? throw new InvalidOperationException("The ETABS 23 seed model is missing from this build.");
        using var reader = new StreamReader(stream);
        return E2kReader.Read(reader.ReadToEnd());
    }
}
