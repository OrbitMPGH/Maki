import { Anchor, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'

const PROXY_DOCS = 'https://github.com/OrbitMPGH/Maki/blob/main/docs/exposing-to-the-internet.md'

/** Shown in place of the offline and push cards when the page is not in a secure context. */
export function SecureContextNote() {
  return (
    <Text size="sm" c="var(--ink-2)">
      <Trans>
        Offline reading and push notifications need Maki to be opened over HTTPS. Browsers switch
        them off on plain HTTP addresses other than localhost. Put Maki behind a reverse proxy with a
        certificate to use them.
      </Trans>{' '}
      <Anchor href={PROXY_DOCS} target="_blank" rel="noreferrer" size="sm">
        <Trans>Reverse proxy setup</Trans>
      </Anchor>
    </Text>
  )
}
