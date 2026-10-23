import { Button, Group, Loader, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLogout } from '../api/auth'
import { useAuth } from '../auth/AuthProvider'
import { useDumpProgress, useMetadataSettings } from '../api/hooks'
import { AuthFrame } from '../components/auth/AuthFrame'
import MetadataDumpProgress, { MetadataDumpStatus } from '../components/MetadataDumpProgress'

/**
 * Stands in front of the whole SPA while the local MangaBaka database is missing: search, adding
 * series, imports and Discover all read from it, and there is no API fallback. Reading apps on
 * OPDS, Komga or KOReader are not behind it, since serving files needs no metadata.
 *
 * An admin sees the live download and can retry it. Everyone else gets a waiting screen; the
 * metadata query polls while the dump is missing, so the app opens by itself once it lands.
 */
export default function MetadataGatePage() {
  const { t } = useLingui()
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const { data: metadata } = useMetadataSettings()
  const { data: progress } = useDumpProgress(isAdmin)
  const logout = useLogout()

  return (
    <AuthFrame
      title={t`Getting the metadata database ready`}
      subtitle={
        <Trans>
          Maki keeps a local copy of the MangaBaka database for search, adding series and Discover.
          It is about a 350 MB download and 3.5 GB on disk, and Maki opens as soon as it is installed.
        </Trans>
      }
      footer={
        <Group justify="center">
          <Button
            variant="subtle"
            color="var(--neutral)"
            size="xs"
            loading={logout.isPending}
            onClick={() => logout.mutate()}
          >
            <Trans>Sign out</Trans>
          </Button>
        </Group>
      }
    >
      <Stack gap="md">
        {isAdmin ? (
          <>
            <MetadataDumpProgress quiet />
            <MetadataDumpStatus metadata={metadata} progress={progress} />
          </>
        ) : (
          <Group gap="sm" wrap="nowrap">
            <Loader size="sm" />
            <Text size="sm">
              <Trans>The server is downloading it now. This page opens Maki by itself when it is done.</Trans>
            </Text>
          </Group>
        )}
        <Text size="xs" c="var(--ink-3)">
          <Trans>Reading apps connected over OPDS, Komga or KOReader keep working in the meantime.</Trans>
        </Text>
      </Stack>
    </AuthFrame>
  )
}
