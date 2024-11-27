﻿using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace OpenVR.NET.Devices;

public class DeviceModel
{
    public readonly string Name;
    public DeviceModel(string name)
    {
        Name = name;
        JoinedModel = new()
        {
            Name = name,
            ModelName = name
        };
    }

    public bool ComponentsLoaded => _components != null;

    /// <summary>
    /// A model which has all components joined together. Individual components this model is made
    /// from can be found in <see cref="Components"/>
    /// </summary>
    public readonly ComponentModel JoinedModel;

    ComponentModel[]? _components;

    /// <summary>
    /// Components that make up the device model. Please note that some of them might be empty objects
    /// used as a reference point such as "base", "tip" "grip"/"handgrip" etc (<see cref="ComponentModel.Name"/>).
    /// There can also be a "status" panel (which Im not sure if youre supposed to render to yourself or if openvr provides a texture?).
    /// If the device is not a controller, this will return <see cref="JoinedModel"/>
    /// </summary>
    public IReadOnlyList<ComponentModel> Components
    {
        get
        {
            if (_components != null)
                return _components;

            var count = Valve.VR.OpenVR.RenderModels.GetComponentCount(Name);
            if (count == 0)
                return _components = new ComponentModel[] { JoinedModel };

            var components = new ComponentModel[(int)count];
            var sbLength = 256;
            var sb = new StringBuilder(256);
            for (int i = 0; i < count; i++)
            {
                sb.Clear();

                var length = Valve.VR.OpenVR.RenderModels.GetComponentName(Name, (uint)i, sb, (uint)sbLength);
                if (length > sbLength)
                {
                    sb.EnsureCapacity(sbLength = (int)length);
                    Valve.VR.OpenVR.RenderModels.GetComponentName(Name, (uint)i, sb, length);
                }

                var name = sb.ToString();
                sb.Clear();
                length = Valve.VR.OpenVR.RenderModels.GetComponentRenderModelName(Name, name, sb, (uint)sbLength);
                if (length > sbLength)
                {
                    sb.EnsureCapacity(sbLength = (int)length);
                    Valve.VR.OpenVR.RenderModels.GetComponentRenderModelName(Name, name, sb, length);
                }

                components[i] = new()
                {
                    Name = name,
                    ModelName = sb.ToString(),
                    ParentName = Name
                };
            }

            return _components = components;
        }
    }
}

public class ComponentModel
{
    /// <summary>
    /// For components other than the main device body, this is the name of the main device
    /// </summary>
    public string? ParentName { get; init; }

    /// <summary>
    /// Name of the component
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Render model name
    /// </summary>
    public string ModelName { get; init; } = string.Empty;

    //static readonly ConcurrentDictionary<string, SemaphoreSlim> modelLoadLocks = new();
    static readonly SemaphoreSlim loadLock = new(1, 1);
    static readonly ConcurrentDictionary<int, SemaphoreSlim> textureLoadLocks = new();
    IntPtr? loadedModelPtr;
    IntPtr? loadedTexturePtr;
    int? loadedTextureIndex;

    public delegate void AddVertex(Vector3 position, Vector3 normal, Vector2 uv);

    public readonly struct Texture
    {
        /// <summary>
        /// A unique ID that represents the texture. You can cache textures with this key to prevent loading them again
        /// </summary>
        public int ID { get; init; }

        /// <summary>
        /// Loads the image into memory and retrieves params to load the data (RGBA32 format - 8 bits per channel).
        /// Please make sure you don't <seealso cref="FreeResources"/> until you're done with this data. Take note
        /// that if the data is <see cref="IntPtr.Zero"/>, the data is invalid.
        /// </summary>
        public delegate Task<(int width, int height, IntPtr data)> ImageParamLoader();

        /// <inheritdoc cref="ImageParamLoader"/>
        public ImageParamLoader ParamsLoader { private get; init; }

        /// <summary>
        /// Loads the image as an ImageSharp image. 
        /// Please make sure you don't <seealso cref="FreeResources"/> until the image is loaded
        /// </summary>
        public async Task<Image<Rgba32>?> LoadImage(bool flipVertically = false)
        {
            var (width, height, pointer) = await LoadParams();

            var data = new byte[width * height * 4];
            Marshal.Copy(pointer, data, 0, data.Length);
            Image<Rgba32> image = new(width, height);

            void ProcessPixels(PixelAccessor<Rgba32> rows)
            {
                var stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    var span = rows.GetRowSpan(flipVertically ? (height - y - 1) : y);
                    var byteSpan = MemoryMarshal.CreateSpan(ref span[0].R, stride);
                    data.AsSpan(stride * y, stride).CopyTo(byteSpan);
                }
            }

            image.ProcessPixelRows(ProcessPixels);

            return image;
        }

        /// <inheritdoc cref="ImageParamLoader"/>
        public Task<(int width, int height, IntPtr data)> LoadParams()
            => ParamsLoader();
    }

    public enum Context
    {
        Model,
        Texture
    }

    public enum ComponentType
    {
        Error,
        Component,
        ReferencePoint,
        Status
    }

    /// <summary>
    /// Loads the model asynchronously (on a thread pool)
    /// </summary>
    public async Task LoadAsync(
        Func<ComponentType, bool>? begin = null,
        Action<ComponentType>? finish = null,
        AddVertex? addVertex = null,
        Action<short, short, short>? addTriangle = null,
        Action<Texture>? addTexture = null,
        Action<EVRRenderModelError, Context>? onError = null)
    {
        // TODO idk. for some reason loading more than one sometimes leads to some of them missing
        var modelLock = loadLock;// modelLoadLocks.GetOrAdd( ModelName, _ => new( 1, 1 ) );
        await modelLock.WaitAsync();

        IntPtr modelPtr = IntPtr.Zero;
        EVRRenderModelError error;
        while ((error = Valve.VR.OpenVR.RenderModels.LoadRenderModel_Async(ModelName, ref modelPtr)) is EVRRenderModelError.Loading)
            await Task.Delay(10);
        
        loadedModelPtr = modelPtr;
        if (error is EVRRenderModelError.None)
        {
            var type = Name == Valve.VR.OpenVR.k_pch_Controller_Component_Status ? ComponentType.Status : ComponentType.Component;
            if (begin?.Invoke(type) == false)
            {
                modelLock.Release();
                return;
            }

            RenderModel_t model = new();

            if (Environment.OSVersion.Platform is PlatformID.MacOSX or PlatformID.Unix)
            {
                var packedModel = Marshal.PtrToStructure<RenderModel_t_Packed>(modelPtr);
                packedModel.Unpack(ref model);
            }
            else
            {
                model = Marshal.PtrToStructure<RenderModel_t>(modelPtr);
            }

            if (addVertex != null)
            {
                var size = Marshal.SizeOf<RenderModel_Vertex_t>();
                for (int i = 0; i < model.unVertexCount; i++)
                {
                    var vertPtr = model.rVertexData + i * size;
                    var vert = Marshal.PtrToStructure<RenderModel_Vertex_t>(vertPtr);

                    addVertex.Invoke(
                        new Vector3(vert.vPosition.v0, vert.vPosition.v1, vert.vPosition.v2),
                        new Vector3(vert.vNormal.v0, vert.vNormal.v1, vert.vNormal.v2),
                        new Vector2(vert.rfTextureCoord0, 1 - vert.rfTextureCoord1)
                    );
                }
            }

            if (addTriangle != null)
            {
                int indexCount = (int)model.unTriangleCount * 3;
                var indices = ArrayPool<short>.Shared.Rent(indexCount);
                Marshal.Copy(model.rIndexData, indices, 0, indexCount);
                for (int i = 0; i < model.unTriangleCount; i++)
                    addTriangle.Invoke(indices[i * 3 + 2], indices[i * 3 + 1], indices[i * 3 + 0]);
                ArrayPool<short>.Shared.Return(indices);
            }

            if (addTexture != null && model.diffuseTextureId >= 0)
            {
                async Task<(int width, int height, IntPtr data)> ParamsLoader()
                {
                    var textureLock = textureLoadLocks.GetOrAdd(model.diffuseTextureId, _ => new(1, 1));
                    await textureLock.WaitAsync();

                    IntPtr texturePtr = IntPtr.Zero;
                    while ((error = Valve.VR.OpenVR.RenderModels.LoadTexture_Async(model.diffuseTextureId, ref texturePtr)) is EVRRenderModelError.Loading)
                        await Task.Delay(10);
                    
                    loadedTextureIndex = model.diffuseTextureId;
                    loadedTexturePtr = texturePtr;
                    if (error is EVRRenderModelError.None)
                    {
                        RenderModel_TextureMap_t texture = new();
                        if (Environment.OSVersion.Platform is PlatformID.MacOSX or PlatformID.Unix)
                        {
                            var packedModel = Marshal.PtrToStructure<RenderModel_TextureMap_t_Packed>(texturePtr);
                            packedModel.Unpack(ref texture);
                        }
                        else
                            texture = Marshal.PtrToStructure<RenderModel_TextureMap_t>(texturePtr);
                        
                        textureLock.Release();
                        return (texture.unWidth, texture.unHeight, texture.rubTextureMapData);
                    }
                    else
                    {
                        onError?.Invoke(error, Context.Texture);
                        textureLock.Release();
                        return (0, 0, IntPtr.Zero);
                    }
                }
                addTexture(new()
                {
                    ID = model.diffuseTextureId,
                    ParamsLoader = ParamsLoader
                });
            }

            finish?.Invoke(type);
        }
        else if (error is EVRRenderModelError.InvalidArg)
        {
            if (begin?.Invoke(ComponentType.ReferencePoint) == false)
            {
                modelLock.Release();
                return;
            }

            finish?.Invoke(ComponentType.ReferencePoint);
        }
        else
        {
            if (begin?.Invoke(ComponentType.Error) == false)
            {
                modelLock.Release();
                return;
            }

            onError?.Invoke(error, Context.Model);
            finish?.Invoke(ComponentType.Error);
        }

        modelLock.Release();
    }

    /// <summary>
    /// Frees unmanaged OpenVR resouces such as the model or texture.
    /// This should be called on all components after all of them have been loaded.
    /// Otherwise you could end up loading then freeing and then loading and freeing the
    /// same resoure sevaral times.
    /// </summary>
    public async void FreeResources()
    {
        if (loadedModelPtr is IntPtr m)
        {
            var modelLock = loadLock;//modelLoadLocks.GetOrAdd( ModelName, _ => new( 1, 1 ) );
            await modelLock.WaitAsync();
            Valve.VR.OpenVR.RenderModels.FreeRenderModel(m);
            loadedModelPtr = null;
            modelLock.Release();
        }

        if (loadedTextureIndex is int i && loadedTexturePtr is IntPtr t)
        {
            var textureLock = textureLoadLocks.GetOrAdd(i, _ => new(1, 1));
            await textureLock.WaitAsync();
            Valve.VR.OpenVR.RenderModels.FreeTexture(t);
            loadedTextureIndex = null;
            loadedTexturePtr = null;
            textureLock.Release();
        }
    }

    public bool HasLoadedResources => loadedModelPtr != null || loadedTextureIndex != null || loadedTexturePtr != null;
}
