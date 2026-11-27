namespace CsiProMax.Model;

/// <summary>Ready-made starting models.</summary>
public static class Templates
{
    /// <summary>
    /// A regular concrete frame building in kN and m: columns on every grid crossing, beams along every grid line,
    /// one slab per bay, fixed supports, and ETABS's own Dead (self weight plus the superimposed dead load) and Live patterns,
    /// with the mass from the Dead pattern.
    /// <paramref name="xSpans"/> and <paramref name="ySpans"/> are the bay sizes; grids are lettered along X and numbered along Y.
    /// </summary>
    public static StructuralModel ConcreteFrame(string title, double[] xSpans, double[] ySpans, int stories, double storyHeight,
        double sdl = 2.0, double live = 3.0)
    {
        if (xSpans.Length == 0 || ySpans.Length == 0 || stories < 1) throw new ArgumentException("Give at least one bay each way and one story.");
        var m = ModelEdit.New(title);
        const string concrete = ModelEdit.DefaultConcrete;
        m.AddRectangularSection("C500x500", concrete, 0.5, 0.5, beam: false);
        m.AddRectangularSection("B300x600", concrete, 0.6, 0.3, beam: true);
        m.AddSlabSection("S200", concrete, 0.2);

        var xs = Cumulative(xSpans); var ys = Cumulative(ySpans);
        for (var i = 0; i < xs.Length; i++) m.AddGrid(Letters(i), "X", xs[i]);
        for (var j = 0; j < ys.Length; j++) m.AddGrid((j + 1).ToString(), "Y", ys[j]);
        foreach (var x in xs) foreach (var y in ys) m.AddSupport(x, y, "Base");

        for (var s = 1; s <= stories; s++)
        {
            var story = m.AddStory($"Story{s}", storyHeight).Name;
            foreach (var x in xs) foreach (var y in ys) m.AddColumn(x, y, story, "C500x500");
            foreach (var y in ys) for (var i = 0; i + 1 < xs.Length; i++) m.AddBeam(xs[i], y, xs[i + 1], y, story, "B300x600");
            foreach (var x in xs) for (var j = 0; j + 1 < ys.Length; j++) m.AddBeam(x, ys[j], x, ys[j + 1], story, "B300x600");
            for (var i = 0; i + 1 < xs.Length; i++)
                for (var j = 0; j + 1 < ys.Length; j++)
                {
                    var slab = m.AddSlab([(xs[i], ys[j]), (xs[i + 1], ys[j]), (xs[i + 1], ys[j + 1]), (xs[i], ys[j + 1])], story, "S200", "D1");
                    if (sdl != 0) m.AddAreaLoad(slab.Label, story, "Dead", sdl);
                    if (live != 0) m.AddAreaLoad(slab.Label, story, "Live", live);
                }
        }
        // The seismic weight takes the superimposed dead load too (ASCE 7 12.7.2, EN 1998-1 3.2.4): the mass from the Dead
        // pattern (self weight plus the superimposed dead), not from the elements as well, which would count the self
        // weight twice. With no superimposed dead load ETABS's own default (the elements) stays.
        if (sdl != 0) new CsiProMax.Lateral.MassSource("MsSrc1", false, true, [("Dead", 1.0)]).WriteTo(m);
        return m;
    }

    private static double[] Cumulative(double[] spans)
    {
        var at = new double[spans.Length + 1];
        for (var i = 0; i < spans.Length; i++)
        {
            if (spans[i] <= 0) throw new ArgumentException("Every bay needs a size above 0.");
            at[i + 1] = at[i] + spans[i];
        }
        return at;
    }

    /// <summary>A, B, ... Z, AA, AB, ...</summary>
    private static string Letters(int index)
    {
        var text = "";
        for (index++; index > 0; index = (index - 1) / 26) text = (char)('A' + (index - 1) % 26) + text;
        return text;
    }
}
