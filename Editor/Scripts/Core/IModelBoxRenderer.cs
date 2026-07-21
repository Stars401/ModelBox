namespace ModelBox
{
    /// <summary>
    /// 管线抽象接口。每个渲染管线（URP、HDRP、Built-in）各一个实现。
    /// ModelBoxManager 通过此接口与底层渲染注入解耦。
    /// </summary>
    public interface IModelBoxRenderer
    {
        bool IsAvailable { get; }
        void Initialize();
        void Shutdown();
        void SetDebugMode(DebugViewMode mode, ModelBoxParameters parameters);
        void SetEnabled(bool enabled);
        bool EnsureSetup();
    }

    /// <summary>
    /// 当前管线不受支持时的空实现。
    /// </summary>
    public class UnsupportedModelBoxRenderer : IModelBoxRenderer
    {
        public bool IsAvailable => false;
        public void Initialize() { }
        public void Shutdown() { }
        public void SetDebugMode(DebugViewMode mode, ModelBoxParameters parameters) { }
        public void SetEnabled(bool enabled) { }
        public bool EnsureSetup() => false;
    }
}
