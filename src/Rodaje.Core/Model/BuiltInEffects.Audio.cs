namespace Rodaje.Core.Model;

/// <summary>Los efectos de inserción de audio del mezclador (formato 7); ninguno admite keyframes.</summary>
public static partial class BuiltInEffects
{
    /// <summary>Puerta de ruido y expansor.</summary>
    public const string Gate = "rodaje.gate";

    public static class GateParameters
    {
        /// <summary>Umbral en dBFS: por encima la puerta se abre.</summary>
        public const string Threshold = "threshold";

        /// <summary>Cuánto baja cerrada, en dB (positivo).</summary>
        public const string Range = "range";

        /// <summary>Relación del expansor (1:N); desde 20, puerta.</summary>
        public const string Ratio = "ratio";

        /// <summary>Ataque al abrir, en ms.</summary>
        public const string Attack = "attack";

        /// <summary>Retención abierta tras bajar del umbral, en ms.</summary>
        public const string Hold = "hold";

        /// <summary>Relajación al cerrar, en ms.</summary>
        public const string Release = "release";

        /// <summary>Se cierra esto por debajo del umbral, en dB.</summary>
        public const string Hysteresis = "hysteresis";

        /// <summary>Pasa altos del detector en Hz (0, apagado).</summary>
        public const string SidechainHighPass = "sidechainHighPass";
    }

    /// <summary>De-esser.</summary>
    public const string DeEsser = "rodaje.deesser";

    public static class DeEsserParameters
    {
        /// <summary>Desde dónde se miden y se bajan las eses, en Hz.</summary>
        public const string Frequency = "frequency";

        public const string Threshold = "threshold";

        /// <summary>Lo más que baja, en dB.</summary>
        public const string Range = "range";

        /// <summary>1: baja solo la banda alta; 0: toda la señal.</summary>
        public const string SplitBand = "splitBand";
    }

    /// <summary>Reverb algorítmica (FDN).</summary>
    public const string Reverb = "rodaje.reverb";

    public static class ReverbParameters
    {
        /// <summary>Predelay en ms.</summary>
        public const string PreDelay = "preDelay";

        /// <summary>Tamaño de la sala en %.</summary>
        public const string Size = "size";

        /// <summary>RT60 en segundos.</summary>
        public const string Decay = "decay";

        /// <summary>Amortiguación de agudos en Hz.</summary>
        public const string Damping = "damping";

        /// <summary>Ancho estéreo en %.</summary>
        public const string Width = "width";

        /// <summary>Pasa altos de la cola en Hz.</summary>
        public const string LowCut = "lowCut";

        /// <summary>Mezcla en %: 50 deja el seco y la cola a tope, 100 solo la cola.</summary>
        public const string Mix = "mix";
    }

    /// <summary>Delay con sincronía al tempo.</summary>
    public const string Delay = "rodaje.delay";

    public static class DelayParameters
    {
        /// <summary>Tiempo en ms (sin sincronía).</summary>
        public const string Time = "time";

        /// <summary>1: el tiempo sale del tempo y la figura.</summary>
        public const string Sync = "sync";

        /// <summary>Pulsos por minuto.</summary>
        public const string Tempo = "tempo";

        /// <summary>Figura: el orden de <c>Rodaje.Audio.Processing.DelayDivision</c> (0 redonda … 9 corchea de tresillo).</summary>
        public const string Division = "division";

        /// <summary>Realimentación en %.</summary>
        public const string Feedback = "feedback";

        /// <summary>1: cada repetición cambia de lado.</summary>
        public const string PingPong = "pingPong";

        public const string LowCut = "lowCut";

        public const string HighCut = "highCut";

        /// <summary>Mezcla en %: 50 deja el seco y el eco a tope.</summary>
        public const string Mix = "mix";
    }

    /// <summary>Saturación suave.</summary>
    public const string Saturation = "rodaje.saturation";

    public static class SaturationParameters
    {
        /// <summary>Empuje en dB.</summary>
        public const string Drive = "drive";

        /// <summary>Carácter en %: 0 cinta (simétrica), 100 válvula (asimétrica).</summary>
        public const string Character = "character";

        /// <summary>Pasa bajos de la salida en Hz (20 000, apagado).</summary>
        public const string Tone = "tone";

        /// <summary>Ganancia de salida en dB.</summary>
        public const string Output = "output";

        /// <summary>Mezcla en paralelo en %.</summary>
        public const string Mix = "mix";
    }

    /// <summary>Ecualizador gráfico de 10 bandas.</summary>
    public const string GraphicEqualizer = "rodaje.graphicEq";

    public static class GraphicEqualizerParameters
    {
        /// <summary>Las bandas en orden, de 31,5 Hz a 16 kHz (dB).</summary>
        public static IReadOnlyList<string> Bands { get; } =
            ["band31", "band63", "band125", "band250", "band500", "band1k", "band2k", "band4k", "band8k", "band16k"];

        /// <summary>Ganancia de salida en dB.</summary>
        public const string Output = "output";
    }

    /// <summary>Cambio de tono sin cambiar la duración.</summary>
    public const string PitchShift = "rodaje.pitch";

    public static class PitchShiftParameters
    {
        public const string Semitones = "semitones";

        public const string Cents = "cents";

        /// <summary>Mezcla en %.</summary>
        public const string Mix = "mix";
    }

    /// <summary>Limitador de pico verdadero por pista.</summary>
    public const string Limiter = "rodaje.limiter";

    public static class LimiterParameters
    {
        /// <summary>Techo en dBTP.</summary>
        public const string Ceiling = "ceiling";

        /// <summary>Ganancia antes del limitador, en dB.</summary>
        public const string InputGain = "inputGain";

        /// <summary>Recuperación en ms.</summary>
        public const string Release = "release";
    }

    /// <summary>Los efectos que van en la cadena de inserciones (sin el volumen): los del mezclador más el ecualizador y el compresor.</summary>
    public static IReadOnlyList<string> AudioInserts { get; } =
        [Equalizer, GraphicEqualizer, Compressor, Gate, DeEsser, Saturation, Delay, Reverb, PitchShift, Limiter];
}
