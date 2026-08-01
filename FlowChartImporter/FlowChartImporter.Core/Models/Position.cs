namespace FlowChartImporter.Core.Models;

public class Position
{
    /// <summary>シート上の行インデックス(1始まり)</summary>
    public int Row { get; set; }

    /// <summary>シート上の列インデックス(1始まり)</summary>
    public int Column { get; set; }

    /// <summary>EMU単位のX座標</summary>
    public double X { get; set; }

    /// <summary>EMU単位のY座標</summary>
    public double Y { get; set; }

    public Position(int row, int column, double x, double y)
    {
        Row = row;
        Column = column;
        X = x;
        Y = y;
    }
}
