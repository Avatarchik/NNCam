using UnityEngine;
using Unity.Barracuda;

namespace NNCam {

sealed class Controller : MonoBehaviour
{
    #region Enum definitions

    enum Architecture { MobileNetV1, ResNet50 }

    #endregion

    #region Editable attributes

    [SerializeField] Architecture _architecture = Architecture.MobileNetV1;
    [SerializeField] Unity.Barracuda.NNModel _model = null;
    [SerializeField, Range(0.01f, 0.99f)] float _threshold = 0.5f;
    [SerializeField, HideInInspector] ComputeShader _converter = null;

    #endregion

    #region Compile-time constants

    // We use a bit strange aspect ratio (20:11) because we have to use 16n+1
    // for these dimension values. It may distort input images a bit, but it
    // might not be a problem for the segmentation models.
    public const int Width = 640 + 1;
    public const int Height = 352 + 1;

    #endregion

    #region Internal objects

    WebCamTexture _webcam;
    ComputeBuffer _buffer;
    IWorker _worker;
    RenderTexture _mask;
    MaterialPropertyBlock _props;

    #endregion

    #region MonoBehaviour implementation

    void Start()
    {
        _webcam = new WebCamTexture();
        _webcam.Play();

        _buffer = new ComputeBuffer(Width * Height * 3, sizeof(float));
        _worker = ModelLoader.Load(_model).CreateWorker();

        _props = new MaterialPropertyBlock();
        _props.SetTexture("_CameraTex", _webcam);
    }

    void OnDisable()
    {
        _buffer?.Dispose();
        _worker?.Dispose();
        _buffer = null;
        _worker = null;
    }

    void OnDestroy()
    {
        if (_webcam != null) Destroy(_webcam);
        if (_mask != null) Destroy(_mask);
    }

    void Update()
    {
        // Check if the last task has been completed.
        if (_worker.scheduleProgress >= 1)
        {
            // Replace the overlay texture with the output.
            if (_mask != null) Destroy(_mask);
            var output = _worker.PeekOutput("float_segments");
            var (w, h) = (output.shape.sequenceLength, output.shape.height);
            using (var segs = output.Reshape(new TensorShape(1, h, w, 1)))
            {
                // Bake into a render texture with normalizing into [0, 1].
                _mask = segs.ToRenderTexture(0, 0, 1.0f / 32, 0.5f);
                _props.SetTexture("_MaskTex", _mask);
            }
        }

        // Image to tensor conversion
        var kernel = (int)_architecture;
        _converter.SetTexture(kernel, "_Texture", _webcam);
        _converter.SetBuffer(kernel, "_Tensor", _buffer);
        _converter.SetInt("_Width", Width);
        _converter.SetInt("_Height", Height);
        _converter.Dispatch(kernel, Width / 8 + 1, Height / 8 + 1, 1);

        // New task scheduling
        using (var tensor = new Tensor(1, Height, Width, 3, _buffer))
            _worker.Execute(tensor);

        // Material property update
        _props.SetFloat("_Threshold", _threshold);
        GetComponent<Renderer>().SetPropertyBlock(_props);
    }

    #endregion
}

} // namespace NNCam