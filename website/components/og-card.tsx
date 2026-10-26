// The Open Graph card next/og renders to a PNG at build time: the dark theme's background, the accent for the site's
// name, no gradient. next/og lays out with flexbox only, so every element with children is a flex container.
export const ogSize = { width: 1200, height: 630 };

export function OgCard({ eyebrow, title, description }: { eyebrow: string; title: string; description?: string }) {
  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'center',
        width: '100%',
        height: '100%',
        padding: '80px',
        backgroundColor: '#0d0d0f',
        color: '#fafafa',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: '20px', marginBottom: '40px' }}>
        <div
          style={{ display: 'flex', width: '56px', height: '56px', borderRadius: '12px', backgroundColor: '#615fff' }}
        />
        <div style={{ display: 'flex', fontSize: '34px', fontWeight: 600, color: '#a5a4ff' }}>{eyebrow}</div>
      </div>
      <div style={{ display: 'flex', fontSize: '72px', fontWeight: 700, lineHeight: 1.1 }}>{title}</div>
      {description ? (
        <div style={{ display: 'flex', marginTop: '28px', fontSize: '32px', lineHeight: 1.4, color: '#d4d4d8' }}>
          {description}
        </div>
      ) : null}
    </div>
  );
}
