use std::env;
use std::io::{self, stdout, Write};
use std::path::{Path, PathBuf};
use std::process;

use crossterm::{
    cursor,
    event::{self, Event, KeyCode, KeyEvent, KeyEventKind},
    execute,
    terminal::{self, EnterAlternateScreen, LeaveAlternateScreen},
};
use image::{imageops, DynamicImage, Rgba};
use chrono::{DateTime, Local};

#[derive(Debug, Clone, Copy, PartialEq)]
enum ColorMode {
    TrueColor,
    Ansi256,
    Ansi16,
}

#[derive(Clone)]
struct Config {
    image_path: PathBuf,
    use_alternate_buffer: bool,
    show_file_info: bool,
    color_mode: ColorMode,
    requested_width: Option<u32>,
    requested_height: Option<u32>,
}

fn print_usage() {
    println!("Usage: ImageViewer [-main|-alternate] [-fileinfo] [-24bit|-truecolor|-256colors|-16colors] <image_path> [<console_width> <console_height>]");
}

fn parse_args() -> Option<Config> {
    let args: Vec<String> = env::args().collect();
    if args.len() < 2 {
        return None;
    }

    let mut use_alternate_buffer = true;
    let mut show_file_info = false;
    let mut color_mode = ColorMode::TrueColor;
    let mut arg_index = 1;

    while arg_index < args.len() {
        match args[arg_index].as_str() {
            "-main" => use_alternate_buffer = false,
            "-alternate" => use_alternate_buffer = true,
            "-fileinfo" => show_file_info = true,
            "-256colors" => color_mode = ColorMode::Ansi256,
            "-16colors" => color_mode = ColorMode::Ansi16,
            "-24bit" | "-24color" | "-truecolor" => color_mode = ColorMode::TrueColor,
            _ if args[arg_index].starts_with('-') => {
                eprintln!("Unknown flag: {}", args[arg_index]);
                return None;
            }
            _ => break,
        }
        arg_index += 1;
    }

    if arg_index >= args.len() {
        return None;
    }

    let image_path = PathBuf::from(&args[arg_index]);
    arg_index += 1;

    let mut requested_width = None;
    let mut requested_height = None;

    if arg_index < args.len() {
        requested_width = args[arg_index].parse().ok();
        arg_index += 1;
    }

    if arg_index < args.len() {
        requested_height = args[arg_index].parse().ok();
    }

    Some(Config {
        image_path,
        use_alternate_buffer,
        show_file_info,
        color_mode,
        requested_width,
        requested_height,
    })
}

fn get_image_files(directory: &Path) -> Vec<PathBuf> {
    let supported_extensions = ["jpg", "jpeg", "png", "bmp", "gif", "tiff", "webp", "ico"];
    let mut files = Vec::new();

    if let Ok(entries) = directory.read_dir() {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_file() {
                if let Some(ext) = path.extension().and_then(|s| s.to_str()) {
                    if supported_extensions.contains(&ext.to_lowercase().as_str()) {
                        files.push(path);
                    }
                }
            }
        }
    }

    files.sort_by(|a, b| {
        a.file_name()
            .unwrap_or_default()
            .cmp(b.file_name().unwrap_or_default())
    });
    files
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let config = match parse_args() {
        Some(c) => c,
        None => {
            print_usage();
            process::exit(1);
        }
    };

    let abs_path = config.image_path.canonicalize().unwrap_or(config.image_path.clone());
    let directory = abs_path.parent().unwrap_or_else(|| Path::new("."));
    let mut image_files = get_image_files(directory);

    let mut current_index = image_files.iter().position(|p| p == &abs_path);
    if current_index.is_none() {
        image_files.push(abs_path.clone());
        image_files.sort_by(|a, b| {
            a.file_name()
                .unwrap_or_default()
                .cmp(b.file_name().unwrap_or_default())
        });
        current_index = image_files.iter().position(|p| p == &abs_path);
    }
    let current_index = current_index.unwrap_or(0);

    // Terminal setup
    if config.use_alternate_buffer {
        execute!(std::io::stdout(), EnterAlternateScreen)?;
    } else {
        clear_screen_ansi()?;
    }
    terminal::enable_raw_mode()?;

    let use_alternate_buffer = config.use_alternate_buffer;
    let res = run_app(config, image_files, current_index);

    // Cleanup
    if use_alternate_buffer {
        // Ensure we leave alternate screen on exit, regardless of how run_app ended
        let _ = execute!(std::io::stdout(), LeaveAlternateScreen);
    }
    terminal::disable_raw_mode()?;

    res
}


fn format_file_size(bytes: u64) -> String {
    let suffixes = ["B", "KB", "MB", "GB", "TB"];
    let mut counter = 0;
    let mut number = bytes as f64;

    while (number / 1024.0).round() >= 1.0 && counter < suffixes.len() - 1 {
        number /= 1024.0;
        counter += 1;
    }

    format!("{:.1}{}", number, suffixes[counter])
}

fn get_closest_ansi_256(r: u8, g: u8, b: u8) -> u8 {
    let r = (r as u16 * 6 / 256) as u8;
    let g = (g as u16 * 6 / 256) as u8;
    let b = (b as u16 * 6 / 256) as u8;
    16 + (r * 36) + (g * 6) + b
}


fn render_image(
    image: &image::ImageBuffer<Rgba<u8>, Vec<u8>>,
    original_width: u32,
    original_height: u32,
    config: &Config,
    image_path: &Path,
    _console_width: u32,
) -> Result<(), Box<dyn std::error::Error>> {
    use std::io::BufWriter;
    let mut out = BufWriter::with_capacity(131072, stdout().lock()); // Increased buffer size
    execute!(out, cursor::Hide, cursor::MoveTo(0, 0))?;

    let metadata = std::fs::metadata(image_path).ok();

    // Pre-calculate info texts to avoid repeated computation
    let mut info_texts = std::collections::HashMap::new();
    if config.show_file_info {
        info_texts.insert(0, image_path.file_name().and_then(|s| s.to_str()).unwrap_or("").to_string());

        if let Some(m) = &metadata {
            info_texts.insert(2, format!("Size: {}", format_file_size(m.len())));

            if let Ok(time) = m.modified() {
                let datetime: DateTime<Local> = time.into();
                info_texts.insert(4, format!("Modified: {}", datetime.format("%Y-%m-%d %H:%M:%S")));
            }
        }

        info_texts.insert(6, format!("Dimensions: {}x{}", original_width, original_height));
    }

    // Convert image to a flat vector for faster access
    let img_data = image.as_raw();
    let img_width = image.width() as usize;

    // Process image in pairs of rows (upper and lower pixels per character cell)
    for y in (0..image.height()).step_by(2) {
        // Create buffer for entire line to minimize write calls
        let mut output_line = String::with_capacity((image.width() * 20) as usize);

        // Check if this row should have info text
        let info_text = if config.show_file_info {
            info_texts.get(&y).cloned()
        } else {
            None
        };

        if let Some(text) = info_text {
            // For the filename row (y=0), append the detected format
            let display_text = if y == 0 {
                if let Some(detected_format) = detect_format_from_header(image_path) {
                    format!("{} (actual format:{})", text, detected_format)
                } else {
                    text
                }
            } else {
                text
            };

            // Process text and image pixels together, but in pairs like the non-text section
            for x in (0..image.width()).step_by(2) {
                // Process first pixel in pair (for text or image)
                let upper_idx = (y as usize * img_width + x as usize) * 4;
                let upper_pixel = Rgba([
                    img_data[upper_idx],
                    img_data[upper_idx + 1],
                    img_data[upper_idx + 2],
                    img_data[upper_idx + 3]
                ]);

                let lower_pixel = if y + 1 < image.height() {
                    let lower_idx = ((y + 1) as usize * img_width + x as usize) * 4;
                    &Rgba([
                        img_data[lower_idx],
                        img_data[lower_idx + 1],
                        img_data[lower_idx + 2],
                        img_data[lower_idx + 3]
                    ])
                } else {
                    &Rgba([0, 0, 0, 255])
                };

                // Handle first character/pixel
                if x < display_text.chars().count() as u32 {
                    // Text position - render character
                    let char_index = x as usize;
                    if char_index < display_text.len() {
                        let ch = display_text.chars().nth(char_index).unwrap_or(' ');

                        // Average the upper and lower pixels for a more accurate background
                        let avg_r = ((upper_pixel[0] as u16 + lower_pixel[0] as u16) / 2) as u8;
                        let avg_g = ((upper_pixel[1] as u16 + lower_pixel[1] as u16) / 2) as u8;
                        let avg_b = ((upper_pixel[2] as u16 + lower_pixel[2] as u16) / 2) as u8;

                        // Create a dimmed version for the background
                        let dim_r = (avg_r as f32 * 0.7) as u8;
                        let dim_g = (avg_g as f32 * 0.7) as u8;
                        let dim_b = (avg_b as f32 * 0.7) as u8;

                        // Print the character with dimmed background
                        match config.color_mode {
                            ColorMode::TrueColor => {
                                output_line.push_str(&format!("\x1b[38;2;255;255;255;48;2;{};{};{}m{}",
                                    dim_r, dim_g, dim_b, ch)); // White text on dimmed background
                            }
                            ColorMode::Ansi256 => {
                                let bg_color = get_closest_ansi_256(dim_r, dim_g, dim_b);
                                output_line.push_str(&format!("\x1b[38;5;15;48;5;{}m{}", bg_color, ch)); // Bright white text (15) on dimmed background
                            }
                            ColorMode::Ansi16 => {
                                // For text in 16-color mode, use a consistent dark background for readability
                                // Instead of trying to dim the pixel colors (which doesn't work well in 16-color mode)
                                output_line.push_str(&format!("\x1b[37;40m{}", ch)); // White text (37) on black background (40)
                            }
                        }
                    }
                } else {
                    // Non-text position - render pixel block
                    append_colored_block_optimized(&mut output_line, upper_pixel, lower_pixel, config.color_mode);
                }

                // Process second pixel in pair if it exists
                if x + 1 < image.width() {
                    let upper_idx2 = (y as usize * img_width + (x + 1) as usize) * 4;
                    let upper_pixel2 = Rgba([
                        img_data[upper_idx2],
                        img_data[upper_idx2 + 1],
                        img_data[upper_idx2 + 2],
                        img_data[upper_idx2 + 3]
                    ]);

                    let lower_pixel2 = if y + 1 < image.height() {
                        let lower_idx2 = ((y + 1) as usize * img_width + (x + 1) as usize) * 4;
                        &Rgba([
                            img_data[lower_idx2],
                            img_data[lower_idx2 + 1],
                            img_data[lower_idx2 + 2],
                            img_data[lower_idx2 + 3]
                        ])
                    } else {
                        &Rgba([0, 0, 0, 255])
                    };

                    // Handle second character/pixel
                    if x + 1 < display_text.chars().count() as u32 {
                        // Text position - render character
                        let char_index = (x + 1) as usize;
                        if char_index < display_text.len() {
                            let ch = display_text.chars().nth(char_index).unwrap_or(' ');

                            // Average the upper and lower pixels for a more accurate background
                            let avg_r = ((upper_pixel2[0] as u16 + lower_pixel2[0] as u16) / 2) as u8;
                            let avg_g = ((upper_pixel2[1] as u16 + lower_pixel2[1] as u16) / 2) as u8;
                            let avg_b = ((upper_pixel2[2] as u16 + lower_pixel2[2] as u16) / 2) as u8;

                            // Create a dimmed version for the background
                            let dim_r = (avg_r as f32 * 0.7) as u8;
                            let dim_g = (avg_g as f32 * 0.7) as u8;
                            let dim_b = (avg_b as f32 * 0.7) as u8;

                            // Print the character with dimmed background
                            match config.color_mode {
                                ColorMode::TrueColor => {
                                    output_line.push_str(&format!("\x1b[38;2;255;255;255;48;2;{};{};{}m{}",
                                        dim_r, dim_g, dim_b, ch)); // White text on dimmed background
                                }
                                ColorMode::Ansi256 => {
                                    let bg_color = get_closest_ansi_256(dim_r, dim_g, dim_b);
                                    output_line.push_str(&format!("\x1b[38;5;15;48;5;{}m{}", bg_color, ch)); // Bright white text (15) on dimmed background
                                }
                                ColorMode::Ansi16 => {
                                    // For text in 16-color mode, use a consistent dark background for readability
                                    // Instead of trying to dim the pixel colors (which doesn't work well in 16-color mode)
                                    output_line.push_str(&format!("\x1b[37;40m{}", ch)); // White text (37) on black background (40)
                                }
                            }
                        }
                    } else {
                        // Non-text position - render pixel block
                        append_colored_block_optimized(&mut output_line, upper_pixel2, lower_pixel2, config.color_mode);
                    }
                }
            }
        } else {
            // No info text, just render image
            for x in (0..image.width()).step_by(2) {
                // Direct indexing for faster pixel access
                let upper_idx = (y as usize * img_width + x as usize) * 4;
                let upper_pixel = Rgba([
                    img_data[upper_idx],
                    img_data[upper_idx + 1],
                    img_data[upper_idx + 2],
                    img_data[upper_idx + 3]
                ]);

                let lower_pixel = if y + 1 < image.height() {
                    let lower_idx = ((y + 1) as usize * img_width + x as usize) * 4;
                    &Rgba([
                        img_data[lower_idx],
                        img_data[lower_idx + 1],
                        img_data[lower_idx + 2],
                        img_data[lower_idx + 3]
                    ])
                } else {
                    &Rgba([0, 0, 0, 255])
                };

                append_colored_block_optimized(&mut output_line, upper_pixel, lower_pixel, config.color_mode);

                if x + 1 < image.width() {
                    let upper_idx2 = (y as usize * img_width + (x + 1) as usize) * 4;
                    let upper_pixel2 = Rgba([
                        img_data[upper_idx2],
                        img_data[upper_idx2 + 1],
                        img_data[upper_idx2 + 2],
                        img_data[upper_idx2 + 3]
                    ]);

                    let lower_pixel2 = if y + 1 < image.height() {
                        let lower_idx2 = ((y + 1) as usize * img_width + (x + 1) as usize) * 4;
                        &Rgba([
                            img_data[lower_idx2],
                            img_data[lower_idx2 + 1],
                            img_data[lower_idx2 + 2],
                            img_data[lower_idx2 + 3]
                        ])
                    } else {
                        &Rgba([0, 0, 0, 255])
                    };

                    append_colored_block_optimized(&mut output_line, upper_pixel2, lower_pixel2, config.color_mode);
                }
            }
        }

        output_line.push_str("\x1b[0m\x1b[K\r\n");
        out.write_all(output_line.as_bytes())?; // More efficient than write! macro
    }

    out.write_all(b"\x1b[J")?; // Clear from cursor to end of screen - use byte string
    execute!(out, cursor::Show)?;
    out.flush()?;
    Ok(())
}

// Optimized helper function to append a block character with upper and lower pixel colors
fn append_colored_block_optimized(output: &mut String, upper_pixel: Rgba<u8>, lower_pixel: &Rgba<u8>, mode: ColorMode) {
    match mode {
        ColorMode::TrueColor => {
            // Direct string formatting to avoid write! overhead
            output.push_str(&format!("\x1b[38;2;{};{};{};48;2;{};{};{}m▄",
                   lower_pixel[0], lower_pixel[1], lower_pixel[2],
                   upper_pixel[0], upper_pixel[1], upper_pixel[2]));
        }
        ColorMode::Ansi256 => {
            output.push_str(&format!("\x1b[38;5;{};48;5;{}m▄",
                   get_closest_ansi_256(lower_pixel[0], lower_pixel[1], lower_pixel[2]),
                   get_closest_ansi_256(upper_pixel[0], upper_pixel[1], upper_pixel[2])));
        }
        ColorMode::Ansi16 => {
            // Lookup table for ANSI codes: index corresponds to palette index
            let ansi_fg_codes = [
                "30", "31", "32", "33", "34", "35", "36", "37",  // Dark colors
                "90", "91", "92", "93", "94", "95", "96", "97"   // Bright colors
            ];
            let ansi_bg_codes = [
                "40", "41", "42", "43", "44", "45", "46", "47",  // Dark colors
                "100", "101", "102", "103", "104", "105", "106", "107"  // Bright colors
            ];

            let upper_code = get_ansi_16_code(upper_pixel[0], upper_pixel[1], upper_pixel[2]);
            let lower_code = get_ansi_16_code(lower_pixel[0], lower_pixel[1], lower_pixel[2]);

            // Swap foreground/background to fix top/bottom reversal issue in 16-color mode
            output.push_str(&format!("\x1b[{};{}m▄",
                ansi_bg_codes[upper_code as usize],  // upper pixel now for background (bottom half)
                ansi_fg_codes[lower_code as usize])); // lower pixel now for foreground (top half)
        }
    }
}

// Helper function to append a block character with upper and lower pixel colors


fn get_ansi_16_code(r: u8, g: u8, b: u8) -> u8 {
    // Define the 16-color palette (standard VGA/CGA colors)
    let palette = [
        (0, 0, 0),           // 0: Black
        (0, 0, 170),         // 1: Blue
        (0, 170, 0),         // 2: Green
        (0, 170, 170),       // 3: Cyan
        (170, 0, 0),         // 4: Red
        (170, 0, 170),       // 5: Magenta
        (170, 85, 0),        // 6: Brown/Yellow
        (170, 170, 170),     // 7: Gray/Light Gray
        (85, 85, 85),       // 8: Dark Gray
        (85, 85, 255),       // 9: Bright Blue
        (85, 255, 85),       // 10: Bright Green
        (85, 255, 255),      // 11: Bright Cyan
        (255, 85, 85),       // 12: Bright Red
        (255, 85, 255),      // 13: Bright Magenta
        (255, 255, 85),      // 14: Bright Yellow
        (255, 255, 255),     // 15: White
    ];

    // Find the closest color using Euclidean distance
    let mut min_distance = f64::MAX;
    let mut best_index = 0;

    for (i, &(pr, pg, pb)) in palette.iter().enumerate() {
        let dr = r as i32 - pr as i32;
        let dg = g as i32 - pg as i32;
        let db = b as i32 - pb as i32;
        let distance = (dr * dr + dg * dg + db * db) as f64;

        if distance < min_distance {
            min_distance = distance;
            best_index = i;
        }
    }

    // Return the actual index (0-15) to preserve dark/bright distinction
    best_index as u8
}

fn run_app(mut config: Config, image_files: Vec<PathBuf>, mut current_index: usize) -> Result<(), Box<dyn std::error::Error>> {
    let mut rotation_step: u32 = 0;
    let mut current_image_path = image_files[current_index].clone();
    let mut original_image = match load_image_safely(&current_image_path) {
        Ok(img) => img,
        Err(e) => {
            eprintln!("Image decode failed: {} - {}", current_image_path.display(), e);
            // Try to load the next image in the list
            let next_index = (current_index + 1) % image_files.len();
            if next_index != current_index {
                current_index = next_index;
                current_image_path = image_files[current_index].clone();
                load_image_safely(&current_image_path)
                    .map_err(|e| format!("Failed to load fallback image {}: {}", current_image_path.display(), e))?
            } else {
                return Err(format!("Failed to load image and no other images available: {}", e).into());
            }
        }
    };
    let mut current_image = original_image.clone();
    let mut resized_cache: Option<image::ImageBuffer<Rgba<u8>, Vec<u8>>> = None;
    let mut console_width_cache: u32 = 0;
    let mut console_height_cache: u32 = 0;
    let mut needs_render = true;

    loop {
        let (width, height) = terminal::size()?;
        let console_width = config.requested_width.unwrap_or(width as u32);
        let console_height = config.requested_height.unwrap_or(height as u32).saturating_sub(1);
        let even_width = if console_width % 2 != 0 { console_width.saturating_sub(1) } else { console_width };

        // Check if terminal size changed (for both width and height)
        let size_changed = needs_render ||
                          resized_cache.is_none() ||
                          even_width != console_width_cache ||
                          console_height != console_height_cache;

        if size_changed {
            // Calculate target dimensions maintaining aspect ratio
            // Account for the fact that each character cell represents 2 vertical pixels
            let image_aspect_ratio = current_image.width() as f32 / current_image.height() as f32;
            // Since each character is 2 pixels tall, the effective terminal resolution is different
            let effective_height = console_height * 2;

            // Calculate dimensions that fit within console bounds
            let mut target_width = even_width;
            let mut target_height = (target_width as f32 / image_aspect_ratio) as u32;

            // If height exceeds effective console height, adjust based on height instead
            if target_height > effective_height {
                target_height = effective_height;
                target_width = (target_height as f32 * image_aspect_ratio) as u32;
            }

            // Ensure we don't upscale beyond original image size unnecessarily
            let final_width = target_width.min(current_image.width());
            let final_height = ((final_width as f32 / image_aspect_ratio) as u32)
                .min(current_image.height());

            // Use Triangle filter for better quality (was Nearest before)
            resized_cache = Some(imageops::resize(&current_image, final_width, final_height, imageops::FilterType::Triangle));
            console_width_cache = even_width;
            console_height_cache = console_height;

            if let Some(ref resized) = resized_cache {
                render_image(resized, current_image.width(), current_image.height(), &config, &current_image_path, even_width)?;
            }
            needs_render = false;
        }

        if let Event::Key(KeyEvent { code, kind, .. }) = event::read()? {
            if kind == KeyEventKind::Press {
                match code {
                    KeyCode::Char('q') | KeyCode::Esc => break,
                    KeyCode::Char('r') => {
                        rotation_step = (rotation_step + 1) % 4;
                        current_image = rotate_image(&original_image, rotation_step);
                        needs_render = true;
                    }
                    KeyCode::Up => {
                        current_index = (current_index + image_files.len() - 1) % image_files.len();
                        current_image_path = image_files[current_index].clone();
                        match load_image_safely(&current_image_path) {
                            Ok(loaded_image) => {
                                original_image = loaded_image;
                                current_image = original_image.clone();
                                rotation_step = 0;
                                needs_render = true;
                            }
                            Err(e) => {
                                eprintln!("Image decode failed: {} - {}", current_image_path.display(), e);
                                // Stay on current image if loading failed
                                needs_render = true;
                            }
                        }
                    }
                    KeyCode::Down => {
                        current_index = (current_index + 1) % image_files.len();
                        current_image_path = image_files[current_index].clone();
                        match load_image_safely(&current_image_path) {
                            Ok(loaded_image) => {
                                original_image = loaded_image;
                                current_image = original_image.clone();
                                rotation_step = 0;
                                needs_render = true;
                            }
                            Err(e) => {
                                eprintln!("Image decode failed: {} - {}", current_image_path.display(), e);
                                // Stay on current image if loading failed
                                needs_render = true;
                            }
                        }
                    }
                    KeyCode::Char('c') => {
                        config.color_mode = match config.color_mode {
                            ColorMode::TrueColor => ColorMode::Ansi256,
                            ColorMode::Ansi256 => ColorMode::Ansi16,
                            ColorMode::Ansi16 => ColorMode::TrueColor,
                        };
                        needs_render = true;
                    }
                    KeyCode::Char('i') => {
                        config.show_file_info = !config.show_file_info;
                        needs_render = true;
                    }
                    _ => {}
                }

                if needs_render {
                    clear_input_buffer()?;
                }
            }
        }
    }

    Ok(())
}

fn clear_input_buffer() -> io::Result<()> {
    while event::poll(std::time::Duration::from_millis(0))? {
        let _ = event::read()?;
    }
    Ok(())
}

fn rotate_image(img: &DynamicImage, step: u32) -> DynamicImage {
    match step {
        1 => img.rotate90(),
        2 => img.rotate180(),
        3 => img.rotate270(),
        _ => img.clone(),
    }
}

fn detect_format_from_header(path: &Path) -> Option<&'static str> {
    use std::fs::File;
    use std::io::Read;

    let mut file = File::open(path).ok()?;
    let mut header = [0u8; 12];
    file.read_exact(&mut header).ok()?;

    // Check for known image signatures
    if header.starts_with(&[0xFF, 0xD8, 0xFF]) {
        Some("JPEG")
    } else if header.starts_with(&[0x89, 0x50, 0x4E, 0x47]) {
        Some("PNG")
    } else if header.starts_with(&[0x47, 0x49, 0x46, 0x38]) {
        Some("GIF")
    } else if header.starts_with(&[0x52, 0x49, 0x46, 0x46]) && header.get(8..12) == Some(&[0x57, 0x45, 0x42, 0x50]) {
        Some("WebP")
    } else if header.starts_with(&[0x42, 0x4D]) {
        Some("BMP")
    } else if header.starts_with(&[0x49, 0x49, 0x2A, 0x00]) || header.starts_with(&[0x4D, 0x4D, 0x00, 0x2A]) {
        Some("TIFF")
    } else if header.len() >= 4 && &header[0..4] == [0x00, 0x00, 0x01, 0x00] {
        Some("ICO")
    } else if header.len() >= 4 && &header[0..4] == [0x00, 0x00, 0x02, 0x00] {
        Some("CUR")
    } else {
        None
    }
}

fn load_image_safely(path: &Path) -> Result<DynamicImage, String> {
    use image::ImageReader;
    use std::io::BufReader;
    use std::fs::File;

    // First, try to load using the standard approach
    let reader_result = ImageReader::open(path);

    match reader_result {
        Ok(reader) => {
            // If the standard approach works, use it
            match reader.decode() {
                Ok(img) => Ok(img),
                Err(_) => {
                    // If decode fails, try format detection approach
                    if let Some(detected_format) = detect_format_from_header(path) {
                        // Try to read with detected format
                        let file = File::open(path)
                            .map_err(|e| format!("Failed to open image file: {}", e))?;
                        let mut buf_reader = BufReader::new(file);

                        let img = {
                            use std::io::Read;
                            let mut buffer = Vec::new();
                            buf_reader.read_to_end(&mut buffer)
                                .map_err(|e| format!("Failed to read image file: {}", e))?;

                            match detected_format {
                                "JPEG" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Jpeg),
                                "PNG" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Png),
                                "GIF" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Gif),
                                "WebP" => image::load_from_memory_with_format(&buffer, image::ImageFormat::WebP),
                                "BMP" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Bmp),
                                "TIFF" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Tiff),
                                "ICO" | "CUR" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Ico),
                                _ => image::load_from_memory(&buffer),
                            }
                        };

                        match img {
                            Ok(decoded_img) => Ok(decoded_img),
                            Err(e) => Err(format!("Image decode failed: {}; detected format: {}", e, detected_format)),
                        }
                    } else {
                        // If detection also fails, return the original error
                        Err(format!("Image decode failed"))
                    }
                }
            }
        }
        Err(_) => {
            // If opening fails, try format detection approach
            if let Some(detected_format) = detect_format_from_header(path) {
                // Try to read with detected format
                let file = File::open(path)
                    .map_err(|e| format!("Failed to open image file: {}", e))?;
                let mut buf_reader = BufReader::new(file);

                let img = {
                    use std::io::Read;
                    let mut buffer = Vec::new();
                    buf_reader.read_to_end(&mut buffer)
                        .map_err(|e| format!("Failed to read image file: {}", e))?;

                    match detected_format {
                        "JPEG" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Jpeg),
                        "PNG" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Png),
                        "GIF" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Gif),
                        "WebP" => image::load_from_memory_with_format(&buffer, image::ImageFormat::WebP),
                        "BMP" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Bmp),
                        "TIFF" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Tiff),
                        "ICO" | "CUR" => image::load_from_memory_with_format(&buffer, image::ImageFormat::Ico),
                        _ => image::load_from_memory(&buffer),
                    }
                };

                match img {
                    Ok(decoded_img) => Ok(decoded_img),
                    Err(e) => Err(format!("Image decode failed: {}; detected format: {}", e, detected_format)),
                }
            } else {
                Err(format!("Failed to open image file"))
            }
        }
    }
}

fn clear_screen_ansi() -> std::io::Result<()> {
    let mut out = stdout();
    // ESC[2J clears screen, ESC[H moves cursor home
    write!(out, "\x1B[2J\x1B[H")?;
    out.flush()
}
