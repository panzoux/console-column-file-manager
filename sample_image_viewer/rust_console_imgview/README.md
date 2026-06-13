# Rust Console Image Viewer

A fast, efficient terminal-based image viewer written in Rust that displays images using half-height blocks (▄) for optimal resolution in the console.

## Features

- **High Resolution Console Display**: Uses half-height blocks (▄) to achieve 2x vertical resolution compared to traditional character-based image viewers
- **True Color Support**: Full 24-bit color rendering with fallback to 256-color and 16-color modes
- **Terminal Resizing**: Automatically adjusts image size when terminal is resized
- **File Navigation**: Browse through all images in a directory using arrow keys
- **Image Rotation**: Rotate images in 90-degree increments
- **File Information**: Displays filename, size, modification date, and dimensions overlaid on the image
- **Format Detection**: Automatically detects image format from file headers
- **Robust Error Handling**: Continues operation even when individual images fail to decode
- **Performance Optimized**: Fast rendering with efficient memory access patterns

## Usage

```bash
# Basic usage
cargo run --release -- [options] <image_path>

# Options:
# -main              : Use main terminal buffer (default is alternate buffer)
# -alternate         : Use alternate terminal buffer (default behavior)
# -fileinfo          : Show file information overlay
# -24bit/-truecolor  : Use 24-bit true color (default)
# -256colors         : Use 256-color mode
# -16colors          : Use 16-color mode
# <console_width> <console_height> : Specify custom console dimensions

# Examples:
./rust_console_imgview -fileinfo image.jpg
./rust_console_imgview -256colors /path/to/image.png
./rust_console_imgview -main -fileinfo /path/to/directory/
```

## Controls

- `q` or `ESC`: Quit the application
- `↑` / `↓`: Navigate to previous/next image in directory
- `r`: Rotate image 90 degrees clockwise
- `c`: Cycle through color modes (TrueColor → 256-color → 16-color → TrueColor)
- `i`: Toggle file information overlay

## Key Improvements

### Performance Optimizations
- **Direct Memory Access**: Replaced expensive `get_pixel()` calls with direct indexing into raw image data
- **Efficient String Operations**: Optimized ANSI escape sequence generation to reduce allocations
- **Enhanced Buffering**: Increased output buffer size and improved write operations
- **Reduced Function Calls**: Consolidated operations to minimize overhead

### Visual Quality Enhancements
- **Half-Height Block Rendering**: Uses ▄ characters to represent 2 vertical pixels per character, doubling effective vertical resolution
- **Dimmed Background for Text**: File info text displays with dimmed pixel backgrounds instead of solid black
- **Aspect Ratio Preservation**: Correctly calculates terminal character aspect ratio for optimal image fitting
- **Improved Filtering**: Uses Triangle filtering for better quality image scaling

### Terminal Integration
- **Proper Buffer Management**: Correctly enters/exits alternate buffer to preserve original terminal content
- **Resize Handling**: Responds to terminal size changes and recalculates image dimensions
- **Robust Cleanup**: Guarantees terminal state restoration on all exit paths

### Error Handling & Reliability
- **Graceful Image Failures**: Continues operation when individual images fail to decode
- **Format Detection**: Identifies actual image format from header bytes and reports mismatches
- **Fallback Mechanisms**: Shows detected format in error messages and filename display
- **Navigation Protection**: Stays on current image if navigation target fails to load

### User Experience
- **File Info Overlay**: Shows filename, size, date, and dimensions with pixel-aware backgrounds
- **Format Display**: Shows actual detected format alongside filename (e.g., "image.jpg (actual format:PNG)")
- **Color Mode Cycling**: Easy switching between TrueColor, 256-color, and 16-color modes
- **Directory Browsing**: Automatic image file discovery and navigation

## Technical Details

The application leverages the following techniques for optimal performance:
- Efficient image scaling algorithms that respect original image dimensions
- Direct memory access patterns to minimize CPU overhead
- Optimized ANSI escape sequence generation for fast terminal rendering
- Proper resource management to prevent memory leaks

## Build Instructions

```bash
# Development build
cargo build

# Release build (optimized)
cargo build --release

# Run directly
cargo run --release -- [options] <image_path>
```

The optimized release binary will be available at `target/release/rust_console_imgview`.

## Supported Formats

The application supports all formats supported by the Rust `image` crate, including:
- JPEG, PNG, GIF, BMP, WebP, TIFF, ICO, and more
- Automatic format detection from file headers
- Error reporting for unsupported or corrupted files