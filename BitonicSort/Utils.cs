using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WebGpuSharp;
using Buffer = WebGpuSharp.Buffer;

namespace BitonicSort;

internal enum StepType : uint
{
	None,
	FlipLocal,
	DisperseLocal,
	FlipGlobal,
	DisperseGlobal,
}

internal enum DisplayMode
{
	Elements,
	SwapHighlight,
}

[StructLayout(LayoutKind.Sequential)]
internal struct ComputeUniforms
{
	public float Width;
	public float Height;
	public uint Algo;
	public uint BlockHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FragmentUniforms
{
	public uint Highlight;
}

internal sealed class BitonicSettings
{
	public uint TotalElements;
	public uint GridWidth;
	public uint GridHeight;
	public uint WorkgroupSize;
	public uint SizeLimit;
	public uint WorkgroupsPerStep;
	public int HoveredCell;
	public int SwappedCell;
	public StepType PrevStep;
	public StepType NextStep;
	public uint PrevSwapSpan;
	public uint NextSwapSpan;
	public uint StepIndex;
	public uint TotalSteps;
	public DisplayMode DisplayMode;
	public uint TotalSwaps;
	public double StepTimeMs;
	public double SortTimeMs;
	public double AverageSortTimeMs;
	public int AutoSortSpeedMs;

	public BitonicSettings(uint totalElements, uint gridWidth, uint gridHeight, uint sizeLimit)
	{
		TotalElements = totalElements;
		GridWidth = gridWidth;
		GridHeight = gridHeight;
		SizeLimit = sizeLimit;
		DisplayMode = DisplayMode.Elements;
		AutoSortSpeedMs = 50;
	}
}

internal sealed class ConfigStats
{
	public uint Sorts;
	public double TotalTimeMs;
}

internal static class BitonicMath
{
	public static uint GetNumSteps(uint elements)
	{
		var n = (uint)Math.Log2(elements);
		return (n * (n + 1)) / 2;
	}

	public static (uint Width, uint Height) GetGridDimensions(uint totalElements)
	{
		var root = Math.Sqrt(totalElements);
		uint width = (uint)(Math.Abs(root % 2) < double.Epsilon
			? Math.Floor(root)
			: Math.Floor(Math.Sqrt(totalElements / 2.0)));
		if (width == 0)
		{
			width = 1;
		}
		uint height = Math.Max(1, totalElements / width);
		return (width, height);
	}

	public static uint ComputeWorkgroupSize(uint totalElements, uint sizeLimit)
	{
		uint half = Math.Max(2, totalElements / 2);
		return Math.Max(2u, Math.Min(half, sizeLimit));
	}

	public static uint ComputeWorkgroupsPerStep(uint totalElements, uint workgroupSize)
	{
		var denom = Math.Max(1u, workgroupSize * 2);
		return Math.Max(1u, (uint)Math.Ceiling(totalElements / (double)denom));
	}
}

internal sealed class TimestampRecorder
{
	private readonly bool _timestampSupported;
	private readonly QuerySet? _querySet;
	private readonly Buffer? _resolveBuffer;
	private readonly Buffer? _readbackBuffer;
	private readonly Action<double> _onStepCompleted;
	private bool _pendingReadback;

	public TimestampRecorder(Device device, Action<double> onStepCompleted)
	{
		_onStepCompleted = onStepCompleted;
		_timestampSupported = device.HasFeature(FeatureName.TimestampQuery);
		if (!_timestampSupported)
		{
			return;
		}

		_querySet = device.CreateQuerySet(new()
		{
			Type = QueryType.Timestamp,
			Count = 2,
		});

		var bufferSize = _querySet.GetCount() * sizeof(ulong);
		_resolveBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)bufferSize,
			Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc,
		});

		_readbackBuffer = device.CreateBuffer(new()
		{
			Size = (ulong)bufferSize,
			Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
		});
	}

	public bool Supported => _timestampSupported;

	public ComputePassEncoder BeginPass(CommandEncoder encoder)
	{
		if (!_timestampSupported)
		{
			return encoder.BeginComputePass();
		}

		ComputePassDescriptor descriptor = new()
		{
			TimestampWrites = new PassTimestampWrites
			{
				QuerySet = _querySet!,
				BeginningOfPassWriteIndex = 0,
				EndOfPassWriteIndex = 1,
			}
		};
		return encoder.BeginComputePass(descriptor);
	}

	public void Resolve(CommandEncoder encoder)
	{
		if (!_timestampSupported)
		{
			return;
		}

		var resolve = _resolveBuffer!;
		var readback = _readbackBuffer!;
		encoder.ResolveQuerySet(_querySet!, 0, _querySet!.GetCount(), resolve, 0);
		if (readback.GetMapState() == BufferMapState.Unmapped)
		{
			encoder.CopyBufferToBuffer(resolve, 0, readback, 0, resolve.GetSize());
		}
		_pendingReadback = true;
	}

	public void TryDownload()
	{
		if (!_timestampSupported || !_pendingReadback)
		{
			return;
		}
		var readback = _readbackBuffer!;
		if (readback.GetMapState() != BufferMapState.Unmapped)
		{
			return;
		}

		_pendingReadback = false;
		_ = readback.MapAsync(MapMode.Read).ContinueWith(task =>
		{
			if (task.Result == MapAsyncStatus.Success)
			{
				readback.GetConstMappedRange<ulong>(0, (nuint)_querySet!.GetCount(), span =>
				{
					var elapsed = span[1] - span[0];
					if (elapsed > 0)
					{
						_onStepCompleted(elapsed / 1_000_000.0);
					}
				});
			}
			readback.Unmap();
		});
	}
}
