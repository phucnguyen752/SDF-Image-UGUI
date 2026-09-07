# SDF Image

Unity 6 / uGUI outlines and soft shadows, with asynchronous SDF baking embedded in the original source sprites.

## Install

In Unity Package Manager, choose **Install package from Git URL**:

```text
https://github.com/phucnguyen752/sdf-image.git#0.3.1
```

Requires Unity 6000.0 and uGUI 2.0.0. The version tag and `upm` branch contain the package at the repository root. The `main` branch contains the Unity development project, with the library in `Assets/SDFImage`.

## Use

Create **GameObject → UI → SDF Image**, assign a source sprite, then select **Generate SDF** if needed. Enable **Outline** or **Shadow** to edit that effect. New images use one component derived from Unity Image.

![URP demo](Assets/SDFImage/Documentation~/preview.png)

- [Usage, API, and limitations](Assets/SDFImage/README.md)
- [Validation results](Assets/SDFImage/VALIDATION.md)
- [Changelog](Assets/SDFImage/CHANGELOG.md)
- [Release workflow](Assets/SDFImage/Documentation~/Publishing.md)

## Development

Open this project with Unity **6000.0.83f1**. Run the `SDFUI.Tests` EditMode suite in Test Runner. Keep all library `.meta` files when moving or updating the package so existing components and sprites retain their identities.
