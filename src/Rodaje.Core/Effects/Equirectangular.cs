namespace Rodaje.Core.Effects;

/// <summary>
/// Reencuadre de video 360 equirectangular a una vista plana (<see cref="Model.BuiltInEffects.Reframe360"/>): la cámara
/// mira con guiñada, cabeceo y alabeo desde el centro de la esfera y ve un plano (rectilínea) o la esfera entera aplanada
/// (estereográfica, el «pequeño planeta»). Es la referencia del shader: si cambia una fórmula, cambia la otra.
/// </summary>
/// <remarks>
/// Ejes: x a la derecha, y arriba y z al frente (el centro del equirectangular). Longitud = atan2(x, z) y latitud =
/// asin(y): u = 0,5 + longitud/2π, v = 0,5 − latitud/π. Guiñada positiva mira a la derecha; cabeceo positivo, arriba;
/// alabeo positivo gira la imagen en sentido horario.
/// </remarks>
public static class Equirectangular
{
    /// <summary>Campo de visión horizontal máximo de la proyección rectilínea: más allá el plano se estira sin sentido.</summary>
    public const double MaxRectilinearFov = 170;

    /// <summary>Los ejes derecha y arriba de la cámara en el mundo (el frente es su producto vectorial).</summary>
    public static (Vector3 Right, Vector3 Up) Basis(double yawDegrees, double pitchDegrees, double rollDegrees)
    {
        double yaw = yawDegrees * Math.PI / 180, pitch = pitchDegrees * Math.PI / 180, roll = rollDegrees * Math.PI / 180;
        var forward = new Vector3(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
        var right = new Vector3(Math.Cos(yaw), 0, -Math.Sin(yaw));
        Vector3 up = Vector3.Cross(forward, right);
        (double c, double s) = (Math.Cos(roll), Math.Sin(roll));
        return ((right * c) + (up * s), (up * c) + (right * -s));
    }

    /// <summary>
    /// Medio ancho del plano de la imagen para el campo de visión horizontal: tan(fov/2) en la rectilínea (hasta
    /// <see cref="MaxRectilinearFov"/>) y 2·tan(fov/4) en la estereográfica.
    /// </summary>
    public static double HalfWidth(double fovDegrees, bool stereographic) => stereographic
        ? 2 * Math.Tan(Math.Clamp(fovDegrees, 1, 350) * Math.PI / 720)
        : Math.Tan(Math.Clamp(fovDegrees, 1, MaxRectilinearFov) * Math.PI / 360);

    /// <summary>Qué punto del equirectangular (u, v de 0 a 1) se ve en el punto (x, y) de la salida (también de 0 a 1, desde arriba).</summary>
    /// <param name="x">De 0 (izquierda) a 1.</param>
    /// <param name="y">De 0 (arriba) a 1.</param>
    /// <param name="yaw">Guiñada en grados.</param>
    /// <param name="pitch">Cabeceo en grados.</param>
    /// <param name="roll">Alabeo en grados.</param>
    /// <param name="fov">Campo de visión horizontal en grados.</param>
    /// <param name="aspect">Ancho / alto de la salida.</param>
    /// <param name="stereographic">Pequeño planeta en lugar de una cámara normal.</param>
    public static (double U, double V) SourceUv(double x, double y, double yaw, double pitch, double roll, double fov, double aspect, bool stereographic)
    {
        double half = HalfWidth(fov, stereographic);
        double px = ((x * 2) - 1) * half, py = (1 - (y * 2)) * half / aspect;
        Vector3 d;
        if (stereographic)
        {
            double r2 = (px * px) + (py * py);
            d = new Vector3(4 * px, 4 * py, 4 - r2) * (1 / (4 + r2));
        }
        else
        {
            d = new Vector3(px, py, 1).Normalized();
        }

        (Vector3 right, Vector3 up) = Basis(yaw, pitch, roll);
        Vector3 world = (right * d.X) + (up * d.Y) + (Vector3.Cross(right, up) * d.Z);
        double longitude = Math.Atan2(world.X, world.Z);
        double latitude = Math.Asin(Math.Clamp(world.Y, -1, 1));
        return (0.5 + (longitude / (2 * Math.PI)), 0.5 - (latitude / Math.PI));
    }

    /// <summary>Un vector de tres componentes en doble precisión.</summary>
    public readonly record struct Vector3(double X, double Y, double Z)
    {
        public static Vector3 operator *(Vector3 v, double k) => new(v.X * k, v.Y * k, v.Z * k);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector3 Cross(Vector3 a, Vector3 b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

        public Vector3 Normalized() => this * (1 / Math.Sqrt((X * X) + (Y * Y) + (Z * Z)));
    }
}
