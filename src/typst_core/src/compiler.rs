use ecow::eco_format;
use typst::diag::StrResult;
use typst_layout::PagedDocument;

/// An image format to export in.
pub enum ImageExportFormat {
    Png,
    Svg,
}

/// Export the frames to PNGs or SVGs.
fn export_image(
    document: &PagedDocument,
    fmt: ImageExportFormat,
    ppi: f32,
) -> StrResult<Vec<Vec<u8>>> {
    let mut buffers = Vec::new();
    for page in document.pages() {
        let buffer = match fmt {
            ImageExportFormat::Png => typst_render::render(
                page,
                &typst_render::RenderOptions {
                    pixel_per_pt: typst::utils::Scalar::new((ppi / 72.0) as f64),
                    render_bleed: false,
                },
            )
            .encode_png()
            .map_err(|err| eco_format!("failed to write PNG file ({err})"))?,
            ImageExportFormat::Svg => {
                let svg = typst_svg::svg(
                    page,
                    &typst_svg::SvgOptions {
                        render_bleed: false,
                        pretty: false,
                    },
                );
                svg.as_bytes().to_vec()
            }
        };
        buffers.push(buffer);
    }
    Ok(buffers)
}

/// Export to a PDF.
#[inline]
pub fn export_pdf(
    document: &PagedDocument,
    standards: &[typst_pdf::PdfStandard],
) -> StrResult<Vec<u8>> {
    // An invalid combination, such as two PDF/A levels or a PDF/A level that
    // contradicts the requested PDF version, has to fail the export. Falling back to
    // the default would hand back an ordinary PDF while reporting success, and the
    // missing conformance would surface only when an archive validator rejects the
    // document, long after it was produced.
    let standards = typst_pdf::PdfStandards::new(standards).map_err(|err| {
        let hints = err
            .hints()
            .iter()
            .map(|hint| hint.as_str())
            .collect::<Vec<_>>()
            .join("; ");

        if hints.is_empty() {
            err.message().clone()
        } else {
            eco_format!("{} (hint: {})", err.message(), hints)
        }
    })?;

    let buffer = typst_pdf::pdf(
        document,
        &typst_pdf::PdfOptions {
            ident: typst::foundations::Smart::Auto,
            timestamp: None, // For reproducible builds
            standards,
            ..Default::default()
        },
    )
    .map_err(|e| eco_format!("failed to export PDF: {:?}", e))?;
    Ok(buffer)
}

pub fn export(
    document: &PagedDocument,
    format: &str,
    ppi: f32,
    standards: &[typst_pdf::PdfStandard],
) -> StrResult<Vec<Vec<u8>>> {
    match format {
        "pdf" => export_pdf(document, standards).map(|pdf| vec![pdf]),
        "png" => export_image(document, ImageExportFormat::Png, ppi),
        "svg" => export_image(document, ImageExportFormat::Svg, ppi),
        _ => Err(eco_format!("unknown export format: {}", format)),
    }
}
