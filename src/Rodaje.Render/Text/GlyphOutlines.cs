using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Render.Text;

/// <summary>
/// El contorno de las letras de un <c>IDWriteTextLayout</c> como geometrías de Direct2D, para el borde. DirectWrite solo
/// entrega los glifos por un <c>IDWriteTextRenderer</c> propio; aquí es una tabla COM escrita a mano con
/// <see cref="UnmanagedCallersOnlyAttribute"/> (apta para Native AOT, sin reflexión) que vive mientras dura <see cref="Collect"/>.
/// </summary>
internal static unsafe class GlyphOutlines
{
    private static readonly void** Vtable = CreateVtable();

    /// <summary>
    /// Las geometrías de los glifos de <paramref name="layout"/> llevadas por <paramref name="transform"/> (del espacio
    /// del layout a píxeles de la textura). Quien llama libera cada una.
    /// </summary>
    public static List<nint> Collect(IDWriteTextLayout* layout, ID2D1Factory* factory, Matrix3x2 transform)
    {
        var geometries = new List<nint>();
        GCHandle handle = GCHandle.Alloc(geometries);
        try
        {
            var context = new Context { Factory = factory, Geometries = GCHandle.ToIntPtr(handle), Transform = transform };
            var renderer = new Renderer { Vtable = Vtable };
            HRESULT hr = layout->Draw(&context, (IDWriteTextRenderer*)&renderer, 0, 0);
            if (hr.FAILED || context.Error.FAILED)
            {
                Release(geometries);
                RenderException.Check(hr.FAILED ? hr : context.Error, "IDWriteTextLayout::Draw(contorno)");
            }
        }
        finally
        {
            handle.Free();
        }

        return geometries;
    }

    public static void Release(List<nint> geometries)
    {
        foreach (nint geometry in geometries)
        {
            ((IUnknown*)geometry)->Release();
        }

        geometries.Clear();
    }

    private static void** CreateVtable()
    {
        void** vtable = (void**)NativeMemory.AllocZeroed((nuint)(10 * sizeof(void*)));
        vtable[0] = (delegate* unmanaged<Renderer*, Guid*, void**, int>)&QueryInterface;
        vtable[1] = (delegate* unmanaged<Renderer*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged<Renderer*, uint>)&ReleaseRenderer;
        vtable[3] = (delegate* unmanaged<Renderer*, void*, BOOL*, int>)&IsPixelSnappingDisabled;
        vtable[4] = (delegate* unmanaged<Renderer*, void*, DWRITE_MATRIX*, int>)&GetCurrentTransform;
        vtable[5] = (delegate* unmanaged<Renderer*, void*, float*, int>)&GetPixelsPerDip;
        vtable[6] = (delegate* unmanaged<Renderer*, Context*, float, float, DWRITE_MEASURING_MODE, DWRITE_GLYPH_RUN*, DWRITE_GLYPH_RUN_DESCRIPTION*, IUnknown*, int>)&DrawGlyphRun;
        vtable[7] = (delegate* unmanaged<Renderer*, void*, float, float, DWRITE_UNDERLINE*, IUnknown*, int>)&DrawUnderline;
        vtable[8] = (delegate* unmanaged<Renderer*, void*, float, float, DWRITE_STRIKETHROUGH*, IUnknown*, int>)&DrawStrikethrough;
        vtable[9] = (delegate* unmanaged<Renderer*, void*, float, float, IDWriteInlineObject*, BOOL, BOOL, IUnknown*, int>)&DrawInlineObject;
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(Renderer* self, Guid* riid, void** result)
    {
        Guid* unknown = __uuidof<IUnknown>();
        Guid* snapping = __uuidof<IDWritePixelSnapping>();
        Guid* renderer = __uuidof<IDWriteTextRenderer>();
        if (*riid == *unknown || *riid == *snapping || *riid == *renderer)
        {
            *result = self;
            return S.S_OK;
        }

        *result = null;
        return E.E_NOINTERFACE;
    }

    // El objeto vive en la pila de Collect: el conteo de referencias no hace nada.
    [UnmanagedCallersOnly]
    private static uint AddRef(Renderer* self) => 1;

    [UnmanagedCallersOnly]
    private static uint ReleaseRenderer(Renderer* self) => 1;

    [UnmanagedCallersOnly]
    private static int IsPixelSnappingDisabled(Renderer* self, void* context, BOOL* disabled)
    {
        *disabled = true;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetCurrentTransform(Renderer* self, void* context, DWRITE_MATRIX* transform)
    {
        *transform = new DWRITE_MATRIX { m11 = 1, m22 = 1 };
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetPixelsPerDip(Renderer* self, void* context, float* pixelsPerDip)
    {
        *pixelsPerDip = 1;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int DrawGlyphRun(Renderer* self, Context* context, float baselineX, float baselineY, DWRITE_MEASURING_MODE mode,
        DWRITE_GLYPH_RUN* run, DWRITE_GLYPH_RUN_DESCRIPTION* description, IUnknown* effect)
    {
        ID2D1PathGeometry* path = null;
        ID2D1GeometrySink* sink = null;
        ID2D1TransformedGeometry* placed = null;
        try
        {
            HRESULT hr = context->Factory->CreatePathGeometry(&path);
            if (hr.SUCCEEDED)
            {
                hr = path->Open(&sink);
            }

            if (hr.SUCCEEDED)
            {
                hr = run->fontFace->GetGlyphRunOutline(run->fontEmSize, run->glyphIndices, run->glyphAdvances, run->glyphOffsets,
                    run->glyphCount, run->isSideways, (run->bidiLevel & 1) != 0, (ID2D1SimplifiedGeometrySink*)sink);
            }

            if (sink != null)
            {
                HRESULT closed = sink->Close();
                hr = hr.SUCCEEDED ? closed : hr;
            }

            if (hr.SUCCEEDED)
            {
                Matrix3x2 m = Matrix3x2.CreateTranslation(baselineX, baselineY) * context->Transform;
                var matrix = new D2D_MATRIX_3X2_F { m11 = m.M11, m12 = m.M12, m21 = m.M21, m22 = m.M22, dx = m.M31, dy = m.M32 };
                hr = context->Factory->CreateTransformedGeometry((ID2D1Geometry*)path, &matrix, &placed);
            }

            if (hr.FAILED)
            {
                context->Error = hr;
                return hr.Value;
            }

            ((List<nint>)GCHandle.FromIntPtr(context->Geometries).Target!).Add((nint)placed);
            placed = null;
            return S.S_OK;
        }
        catch (Exception error)
        {
            context->Error = error.HResult;
            return E.E_FAIL;
        }
        finally
        {
            if (placed != null)
            {
                placed->Release();
            }

            if (sink != null)
            {
                sink->Release();
            }

            if (path != null)
            {
                path->Release();
            }
        }
    }

    [UnmanagedCallersOnly]
    private static int DrawUnderline(Renderer* self, void* context, float x, float y, DWRITE_UNDERLINE* underline, IUnknown* effect) => S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawStrikethrough(Renderer* self, void* context, float x, float y, DWRITE_STRIKETHROUGH* strikethrough, IUnknown* effect) =>
        S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawInlineObject(Renderer* self, void* context, float x, float y, IDWriteInlineObject* inline, BOOL sideways,
        BOOL rightToLeft, IUnknown* effect) => S.S_OK;

    /// <summary>El objeto COM: solo el puntero a la tabla.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Renderer
    {
        public void** Vtable;
    }

    /// <summary>Lo que <see cref="DrawGlyphRun"/> necesita, pasado como <c>clientDrawingContext</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Context
    {
        public ID2D1Factory* Factory;
        public nint Geometries;
        public Matrix3x2 Transform;
        public HRESULT Error;
    }
}
