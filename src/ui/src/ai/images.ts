// Reference images the user attaches to a prompt (file picker, paste, drag and drop).
// Each is downscaled and re-encoded as a JPEG data URL: keeps requests small and works with every vision model,
// whatever the source format (PNG, WebP, HEIC the web view can decode, ...).

export interface Attachment {
  /** original file name, for the chat and the run log */
  name: string
  dataUrl: string
}

/** Longest side in px. Anthropic recommends ≤ 1568; larger images are downscaled by the provider anyway. */
export const MAX_IMAGE_SIDE = 1568
/** Reference images per prompt. */
export const MAX_ATTACHMENTS = 4
/** Raw file size accepted before decoding. */
const MAX_FILE_BYTES = 25 * 1024 * 1024

export function isImageFile(file: File): boolean {
  return file.type.startsWith('image/')
}

/** Decodes, downscales and re-encodes one image file. Throws with a user-facing message. */
export async function readImageFile(file: File): Promise<Attachment> {
  if (!isImageFile(file)) throw new Error(`${file.name || 'file'} is not an image`)
  if (file.size > MAX_FILE_BYTES) throw new Error(`${file.name || 'image'} is larger than 25 MB`)
  const url = URL.createObjectURL(file)
  try {
    const img = await load(url)
    const scale = Math.min(1, MAX_IMAGE_SIDE / Math.max(img.naturalWidth, img.naturalHeight))
    const w = Math.max(1, Math.round(img.naturalWidth * scale))
    const h = Math.max(1, Math.round(img.naturalHeight * scale))
    const canvas = document.createElement('canvas')
    canvas.width = w
    canvas.height = h
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('cannot process images in this panel')
    ctx.fillStyle = '#ffffff' // transparent areas become white, not black
    ctx.fillRect(0, 0, w, h)
    ctx.drawImage(img, 0, 0, w, h)
    return { name: file.name || 'pasted image', dataUrl: canvas.toDataURL('image/jpeg', 0.9) }
  } finally {
    URL.revokeObjectURL(url)
  }
}

function load(url: string) {
  return new Promise<HTMLImageElement>((resolve, reject) => {
    const img = new Image()
    img.onload = () => (img.naturalWidth && img.naturalHeight ? resolve(img) : reject(new Error('empty image')))
    img.onerror = () => reject(new Error('could not read the image (unsupported format?)'))
    img.src = url
  })
}

/** "data:image/jpeg;base64,..." → "jpg" (run log file names). */
export function dataUrlExtension(dataUrl: string): string {
  const mime = dataUrl.match(/^data:image\/([a-z0-9.+-]+)[;,]/i)?.[1]?.toLowerCase() ?? 'png'
  return mime === 'jpeg' ? 'jpg' : mime.replace(/[^a-z0-9]/g, '') || 'png'
}
