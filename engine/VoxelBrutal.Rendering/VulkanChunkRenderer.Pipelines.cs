using System.Text;
using Brutal.Strings;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace VoxelBrutal.Rendering;

public sealed unsafe partial class VulkanChunkRenderer
{
    private void CreatePipelines()
    {
        var vertex = CompileShader("chunk.vert.glsl", ShaderKind.VertexShader);
        VkShaderModule fragment = default, compute = default, pyramid = default;
        try
        {
            fragment = CompileShader("chunk.frag.glsl", ShaderKind.FragmentShader);
            compute = CompileShader("cull.comp.glsl", ShaderKind.ComputeShader);
            pyramid = CompileShader("pyramid.comp.glsl", ShaderKind.ComputeShader);
            byte* main = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new() { Stage = VkShaderStageFlags.VertexBit, Module = vertex, Name = main };
            stages[1] = new() { Stage = VkShaderStageFlags.FragmentBit, Module = fragment, Name = main };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo();
            var assembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo { PolygonMode = VkPolygonMode.Fill,
                CullMode = VkCullModeFlags.BackBit, FrontFace = VkFrontFace.CounterClockwise, LineWidth = 1 };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depth = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = VkCompareOp.GreaterOrEqual };
            var colorAttachment = new VkPipelineColorBlendAttachmentState { ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit };
            var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &colorAttachment };
            var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
            var colorFormat = _colorFormat;
            var rendering = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = &colorFormat, DepthAttachmentFormat = VkFormat.D32SFloat };
            var graphics = new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering, StageCount = 2, Stages = stages, VertexInputState = &vertexInput,
                InputAssemblyState = &assembly, ViewportState = &viewport, RasterizationState = &raster,
                MultisampleState = &multisample, DepthStencilState = &depth, ColorBlendState = &blend,
                DynamicState = &dynamic, Layout = _pipelineLayout,
            };
            VkPipeline pipeline;
            Check(_device.CreateGraphicsPipelines(default, new ReadOnlySpan<VkGraphicsPipelineCreateInfo>(&graphics, 1), null, new Span<VkPipeline>(&pipeline, 1)), "create graphics pipeline");
            _graphicsPipeline = pipeline;
            var computeInfo = new VkComputePipelineCreateInfo { Layout = _pipelineLayout,
                Stage = new() { Stage = VkShaderStageFlags.ComputeBit, Module = compute, Name = main } };
            Check(_device.CreateComputePipelines(default, new ReadOnlySpan<VkComputePipelineCreateInfo>(&computeInfo, 1), null, new Span<VkPipeline>(&pipeline, 1)), "create culling pipeline");
            _cullPipeline = pipeline;
            computeInfo = new VkComputePipelineCreateInfo { Layout = _pyramidPipelineLayout,
                Stage = new() { Stage = VkShaderStageFlags.ComputeBit, Module = pyramid, Name = main } };
            Check(_device.CreateComputePipelines(default, new ReadOnlySpan<VkComputePipelineCreateInfo>(&computeInfo, 1), null,
                new Span<VkPipeline>(&pipeline, 1)), "create depth reduction pipeline");
            _pyramidPipeline = pipeline;
        }
        finally
        {
            _device.DestroyShaderModule(vertex, null);
            if (!fragment.IsNull()) _device.DestroyShaderModule(fragment, null);
            if (!compute.IsNull()) _device.DestroyShaderModule(compute, null);
            if (!pyramid.IsNull()) _device.DestroyShaderModule(pyramid, null);
        }
    }

    private VkShaderModule CompileShader(string name, ShaderKind kind)
    {
        var assembly = typeof(VulkanChunkRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream($"VoxelBrutal.Rendering.Shaders.{name}")
            ?? throw new InvalidOperationException($"Embedded shader missing: {name}");
        using var reader = new StreamReader(stream);
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        options.SetOptimizationLevel(OptimizationLevel.Performance);
        // 0x00403000 = Vulkan 1.3. SPIR-V 1.0 is sufficient for these deliberately portable shaders.
        options.SetTargetEnv(TargetEnv.Vulkan, Vulkan13);
        var filename = Encoding.UTF8.GetBytes(name + '\0');
        using var result = compiler.Compile(Encoding.UTF8.GetBytes(reader.ReadToEnd()), kind,
            new RefString8(filename), new RefString8("main\0"u8), options);
        return _device.CreateShaderModule(new VkShaderModuleCreateInfo { CodeSize = result.GetLength(), Code = (int*)result.GetBytes() }, null);
    }
}
