using System.Diagnostics;
using Rodaje.App.Views.Player;

namespace Rodaje.App.Tests.Player;

/// <summary>
/// La GPU que se pierde a mitad de reproducción (TDR, DEVICE_REMOVED): el hilo de render rehace el dispositivo y sigue en el
/// mismo cuadro. Aquí con un dispositivo simulado y el mismo bucle que <c>PlayerEngine.Run</c>: un fallo con la GPU perdida
/// se recupera; uno con la GPU sana, o una GPU que se cae en bucle, para el visor con el error.
/// </summary>
public class GpuRecoveryTests
{
    private static readonly long Second = Stopwatch.Frequency;

    /// <summary>Lo mínimo del motor: un dispositivo que se puede perder, un cabezal y los avisos.</summary>
    private sealed class SimulatedViewer
    {
        public sealed class Device
        {
            public bool IsLost { get; set; }
        }

        private readonly GpuRecovery _recovery = new();

        public Device Gpu { get; private set; } = new();

        public int Devices { get; private set; } = 1;

        public long Frame { get; private set; }

        public List<string> Warnings { get; } = [];

        public string? Failure { get; private set; }

        public Func<long, Exception?> FaultAt { get; set; } = _ => null;

        public long Now { get; set; }

        /// <summary>El bucle de PlayerEngine.Run, con <paramref name="frames"/> cuadros por vuelta.</summary>
        public void Run(long frames)
        {
            long end = Frame + frames;
            try
            {
                while (Frame < end)
                {
                    try
                    {
                        while (Frame < end)
                        {
                            if (Gpu.IsLost)
                            {
                                throw new InvalidOperationException("Se perdió la tarjeta gráfica.");
                            }

                            if (FaultAt(Frame) is { } fault)
                            {
                                throw fault;
                            }

                            Frame++;
                        }
                    }
                    catch (Exception) when (_recovery.ShouldRecover(Gpu.IsLost, Now))
                    {
                        // RecoverGpu: dispositivo nuevo, misma posición.
                        Gpu = new Device();
                        Devices++;
                        Warnings.Add(GpuRecovery.Notice);
                    }
                }
            }
#pragma warning disable CA1031 // Como el motor: el error queda para el visor.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Failure = ex.Message;
            }
        }
    }

    [Fact]
    public void Una_GPU_que_se_pierde_se_rehace_y_sigue_en_el_mismo_cuadro()
    {
        var viewer = new SimulatedViewer();
        viewer.FaultAt = f =>
        {
            if (f == 120 && viewer.Devices == 1)
            {
                viewer.Gpu.IsLost = true;
                return new InvalidOperationException("Present devolvió DXGI_ERROR_DEVICE_REMOVED.");
            }

            return null;
        };

        viewer.Run(300);

        Assert.Null(viewer.Failure);
        Assert.Equal(300, viewer.Frame);
        Assert.Equal(2, viewer.Devices);
        Assert.Equal(GpuRecovery.Notice, Assert.Single(viewer.Warnings));
    }

    [Fact]
    public void Una_GPU_perdida_sin_excepcion_tambien_se_rehace()
    {
        var viewer = new SimulatedViewer();
        viewer.FaultAt = f =>
        {
            if (f == 10 && viewer.Devices == 1)
            {
                viewer.Gpu.IsLost = true;
            }

            return null;
        };

        viewer.Run(30);

        Assert.Null(viewer.Failure);
        Assert.Equal(30, viewer.Frame);
        Assert.Equal(2, viewer.Devices);
    }

    [Fact]
    public void Un_error_con_la_GPU_sana_para_el_visor()
    {
        var viewer = new SimulatedViewer { FaultAt = f => f == 5 ? new FormatException("otro fallo") : null };

        viewer.Run(30);

        Assert.Equal("otro fallo", viewer.Failure);
        Assert.Equal(1, viewer.Devices);
        Assert.Empty(viewer.Warnings);
    }

    [Fact]
    public void Una_GPU_que_se_cae_en_bucle_se_rinde_al_cuarto_intento_en_un_minuto()
    {
        var viewer = new SimulatedViewer();
        viewer.FaultAt = _ =>
        {
            viewer.Gpu.IsLost = true;
            viewer.Now += Second;
            return null;
        };

        viewer.Run(10);

        Assert.Equal("Se perdió la tarjeta gráfica.", viewer.Failure);
        Assert.Equal(1 + GpuRecovery.MaxRecoveries, viewer.Devices);
    }

    [Fact]
    public void Pasado_el_minuto_vuelve_a_haber_intentos()
    {
        var recovery = new GpuRecovery();
        for (int i = 0; i < GpuRecovery.MaxRecoveries; i++)
        {
            Assert.True(recovery.ShouldRecover(true, i * Second));
        }

        Assert.False(recovery.ShouldRecover(true, 30 * Second));
        Assert.True(recovery.ShouldRecover(true, 61 * Second));
        Assert.False(recovery.ShouldRecover(false, 200 * Second));
    }
}
