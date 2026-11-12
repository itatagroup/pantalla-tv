namespace PantallaTv.Emisor.Tests;

public class RedTests
{
    [Fact]
    public void Sin_redes_publicas_no_hay_aviso() => Assert.Null(Red.Aviso([]));

    [Fact]
    public void El_aviso_nombra_las_redes_publicas_y_dice_que_hacer()
    {
        var aviso = Red.Aviso(["Cafe", "Hotel"]);
        Assert.NotNull(aviso);
        Assert.Contains("«Cafe», «Hotel»", aviso);
        Assert.Contains("privada", aviso);
        Assert.Contains("IP", aviso);
    }
}
