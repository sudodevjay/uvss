"""Exports a pretrained (ImageNet) ResNet18's layer2+layer3 features as one
ONNX graph -- the standard PatchCore anomaly-detection backbone. No custom
training happens here: the weights are the stock torchvision pretrained
ones, used purely as a generic visual-feature extractor. The "training" for
anomaly detection instead happens on the C# side, at runtime, by building a
memory bank of these features from a folder of known-clean chassis images
(PatchCoreAnomalyDetector.BuildMemoryBank) -- no threat-object photos are
ever needed, only clean ones.

Pipeline baked into the exported graph, so the C# side only has to feed a
plain 0..1-range CHW RGB tensor (same preprocessing YoloDecoder.
MatToChwFloatArray already produces for the other detectors in this
project):
  1. ImageNet normalization (mean/std) -- so the pretrained weights see the
     input distribution they were actually trained on.
  2. ResNet18 stem + layer1 + layer2 + layer3.
  3. 3x3 avg-pool (stride 1, pad 1) on each of layer2/layer3's output --
     local neighbourhood aggregation, exactly as the PatchCore paper does,
     smooths single-pixel noise out of the patch features.
  4. Upsample layer3's output to layer2's spatial resolution (bilinear).
  5. Concatenate channel-wise -> one [1, 384, 28, 28] feature grid for a
     224x224 input (128 channels from layer2 + 256 from layer3).

Each of the 28x28=784 spatial positions in that output is one "patch
feature vector" (384-dim) -- the unit PatchCoreAnomalyDetector compares
against its memory bank of known-clean patches.
"""
import torch
import torch.nn as nn
import torch.nn.functional as F
import torchvision


class PatchCoreBackbone(nn.Module):
    def __init__(self):
        super().__init__()
        resnet = torchvision.models.resnet18(weights=torchvision.models.ResNet18_Weights.IMAGENET1K_V1)
        resnet.eval()
        self.stem = nn.Sequential(resnet.conv1, resnet.bn1, resnet.relu, resnet.maxpool)
        self.layer1 = resnet.layer1
        self.layer2 = resnet.layer2
        self.layer3 = resnet.layer3
        self.register_buffer("mean", torch.tensor([0.485, 0.456, 0.406]).view(1, 3, 1, 1))
        self.register_buffer("std", torch.tensor([0.229, 0.224, 0.225]).view(1, 3, 1, 1))

    def forward(self, x):
        x = (x - self.mean) / self.std
        x = self.stem(x)
        x = self.layer1(x)
        f2 = self.layer2(x)
        f3 = self.layer3(f2)

        f2 = F.avg_pool2d(f2, kernel_size=3, stride=1, padding=1)
        f3 = F.avg_pool2d(f3, kernel_size=3, stride=1, padding=1)
        f3_up = F.interpolate(f3, size=f2.shape[-2:], mode="bilinear", align_corners=False)
        return torch.cat([f2, f3_up], dim=1)


def main():
    model = PatchCoreBackbone()
    model.eval()
    dummy = torch.randn(1, 3, 224, 224)
    with torch.no_grad():
        out = model(dummy)
    print(f"Output shape for a 224x224 input: {tuple(out.shape)}")

    onnx_path = "patchcore_backbone.onnx"
    torch.onnx.export(
        model,
        dummy,
        onnx_path,
        input_names=["image"],
        output_names=["features"],
        dynamic_axes={"image": {2: "height", 3: "width"}, "features": {2: "fh", 3: "fw"}},
        opset_version=17,
    )
    print(f"Exported to {onnx_path}")


if __name__ == "__main__":
    main()
