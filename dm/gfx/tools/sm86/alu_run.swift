// Headless Metal runner for translated SM86 test programs (docs/reference/shaders-sm86.md, Tests): renders one fragment program offscreen and
// writes its colour target 0 raw. Build: xcrun swiftc -O alu_run.swift -o <work dir>/alu_run (outside the repository).
//   alu_run <program.metal> <out.bin> [--size W H] [--format u32|f32] [--math safe|relaxed|fast] [--buf <id> <file>]...
// The program is the spirv-cross MSL of a fragment program (the harness's options, --msl-argument-buffers among them). A fullscreen triangle covers a
// W x H target (default 1 x 1) of RGBA32Uint (u32, the default) or RGBA32Float; out.bin holds 16 bytes per pixel, row-major. --buf binds a file's
// bytes in the fragment's argument buffer at [[id(<id>)]] (the SPIR-V binding the translator assigned), or at [[buffer(<id>)]] for a plain buffer
// argument; every other buffer the program declares gets 64 KiB of zeros. --math picks the compiler's math mode (default safe: IEEE NaN / infinity
// behaviour; the chain compiles with the default fast mode on the Mac and relaxed on iOS).
import Foundation
import Metal

func fail(_ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}

func number(_ text: String) -> Int {
    guard let value = Int(text) else {
        fail("not a number: \(text)")
    }

    return value
}

func contents(_ path: String) -> Data {
    guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else {
        fail("cannot read \(path)")
    }

    return data
}

var args = Array(CommandLine.arguments.dropFirst())

if args.count < 2 {
    fail("usage: alu_run <program.metal> <out.bin> [--size W H] [--format u32|f32] [--math safe|relaxed|fast] [--buf <id> <file>]...")
}

let mslPath = args.removeFirst()
let outPath = args.removeFirst()
var width = 1
var height = 1
var format = "u32"
var math = "safe"
var files: [Int: Data] = [:]

while !args.isEmpty {
    let option = args.removeFirst()

    func next() -> String {
        if args.isEmpty {
            fail("\(option) needs a value")
        }

        return args.removeFirst()
    }

    switch option {
    case "--size":
        width = number(next())
        height = number(next())
    case "--format":
        format = next()
    case "--math":
        math = next()
    case "--buf":
        let id = number(next())
        files[id] = contents(next())
    default:
        fail("unknown option \(option)")
    }
}

guard let device = MTLCreateSystemDefaultDevice() else {
    fail("no Metal device")
}

let vertexSource = """
    #include <metal_stdlib>
    using namespace metal;
    struct AluRunVertexOut { float4 position [[position]]; };
    vertex AluRunVertexOut alu_run_vs(uint vid [[vertex_id]])
    {
        AluRunVertexOut o;
        float2 p = float2(float((vid << 1) & 2), float(vid & 2));
        o.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
        return o;
    }
    """

let options = MTLCompileOptions()

switch math {
case "fast":
    options.mathMode = .fast
case "relaxed":
    options.mathMode = .relaxed
case "safe":
    options.mathMode = .safe
default:
    fail("unknown math mode \(math)")
}

let fragmentSource = String(decoding: contents(mslPath), as: UTF8.self)
let fragmentLibrary: MTLLibrary
let vertexLibrary: MTLLibrary

do {
    fragmentLibrary = try device.makeLibrary(source: fragmentSource, options: options)
    vertexLibrary = try device.makeLibrary(source: vertexSource, options: options)
} catch {
    fail("compile: \(error)")
}

guard let fragmentName = fragmentLibrary.functionNames.first(where: { fragmentLibrary.makeFunction(name: $0)?.functionType == .fragment }),
    let fragment = fragmentLibrary.makeFunction(name: fragmentName)
else {
    fail("no fragment function in \(mslPath)")
}

let descriptor = MTLRenderPipelineDescriptor()
descriptor.vertexFunction = vertexLibrary.makeFunction(name: "alu_run_vs")
descriptor.fragmentFunction = fragment
descriptor.colorAttachments[0].pixelFormat = format == "f32" ? .rgba32Float : .rgba32Uint

let pipeline: MTLRenderPipelineState
let reflection: MTLRenderPipelineReflection?

do {
    (pipeline, reflection) = try device.makeRenderPipelineState(descriptor: descriptor, options: [.bindingInfo, .bufferTypeInfo])
} catch {
    fail("pipeline: \(error)")
}

func makeBuffer(_ data: Data?) -> MTLBuffer {
    let length = max(data?.count ?? 0, 65536)
    let buffer = device.makeBuffer(length: length, options: .storageModeShared)!
    memset(buffer.contents(), 0, length)

    if let data = data {
        data.withUnsafeBytes { buffer.contents().copyMemory(from: $0.baseAddress!, byteCount: data.count) }
    }

    return buffer
}

// Bindings: argument buffers get every pointer member filled (the given file or zeros); plain buffers likewise.
var vertexBound: [(MTLBuffer, Int)] = []
var resources: [MTLBuffer] = []

for binding in reflection?.fragmentBindings ?? [] where binding.type == .buffer {
    guard let bufferBinding = binding as? MTLBufferBinding else {
        continue
    }

    if let members = bufferBinding.bufferStructType?.members, members.contains(where: { $0.dataType == .pointer }) {
        let encoder = fragment.makeArgumentEncoder(bufferIndex: binding.index)
        let argumentBuffer = device.makeBuffer(length: max(encoder.encodedLength, 16), options: .storageModeShared)!
        encoder.setArgumentBuffer(argumentBuffer, offset: 0)

        for member in members where member.dataType == .pointer {
            let buffer = makeBuffer(files[member.argumentIndex])
            encoder.setBuffer(buffer, offset: 0, index: member.argumentIndex)
            resources.append(buffer)
        }

        vertexBound.append((argumentBuffer, binding.index))
    } else {
        vertexBound.append((makeBuffer(files[binding.index]), binding.index))
    }
}

let textureDescriptor = MTLTextureDescriptor.texture2DDescriptor(
    pixelFormat: descriptor.colorAttachments[0].pixelFormat, width: width, height: height, mipmapped: false)
textureDescriptor.usage = [.renderTarget]
textureDescriptor.storageMode = .shared

let target = device.makeTexture(descriptor: textureDescriptor)!
let pass = MTLRenderPassDescriptor()
pass.colorAttachments[0].texture = target
pass.colorAttachments[0].loadAction = .clear
pass.colorAttachments[0].storeAction = .store
pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)

let queue = device.makeCommandQueue()!
let commands = queue.makeCommandBuffer()!
let encoder = commands.makeRenderCommandEncoder(descriptor: pass)!

encoder.setRenderPipelineState(pipeline)

for (buffer, index) in vertexBound {
    encoder.setFragmentBuffer(buffer, offset: 0, index: index)
}

for buffer in resources {
    encoder.useResource(buffer, usage: .read, stages: .fragment)
}

encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
encoder.endEncoding()
commands.commit()
commands.waitUntilCompleted()

if let error = commands.error {
    fail("gpu: \(error)")
}

var pixels = Data(count: width * height * 16)

pixels.withUnsafeMutableBytes {
    target.getBytes($0.baseAddress!, bytesPerRow: width * 16, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
}

do {
    try pixels.write(to: URL(fileURLWithPath: outPath))
} catch {
    fail("write \(outPath): \(error)")
}
