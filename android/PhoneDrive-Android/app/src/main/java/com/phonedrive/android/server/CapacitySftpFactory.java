package com.phonedrive.android.server;

import java.io.IOException;
import java.util.function.Supplier;
import org.apache.sshd.common.util.buffer.Buffer;
import org.apache.sshd.server.channel.ChannelSession;
import org.apache.sshd.server.command.Command;
import org.apache.sshd.sftp.common.SftpConstants;
import org.apache.sshd.sftp.server.SftpSubsystem;
import org.apache.sshd.sftp.server.SftpSubsystemFactory;

/** OpenSSH capacity extension; values come from the actual shared filesystem. */
public final class CapacitySftpFactory extends SftpSubsystemFactory {
    private final Supplier<long[]> capacity;
    public CapacitySftpFactory(Supplier<long[]> capacity) { this.capacity = capacity; }
    @Override public Command createSubsystem(ChannelSession channel) throws IOException {
        SftpSubsystem subsystem = new SftpSubsystem(channel, this) {
            @Override protected void appendExtensions(Buffer buffer, String version) {
                super.appendExtensions(buffer, version);
                buffer.putString("statvfs@openssh.com"); buffer.putString("2");
            }
            @Override protected void executeExtendedCommand(Buffer buffer, int id, String extension) throws IOException {
                if (!"statvfs@openssh.com".equals(extension)) { super.executeExtendedCommand(buffer, id, extension); return; }
                buffer.getString(); // Virtual share has one backing filesystem; never resolve a raw host path.
                try {
                    long[] s = capacity.get();
                    if (s.length != 4 || s[0] <= 0 || s[1] <= 0 || s[2] < 0 || s[3] < 0 || s[2] > s[1] || s[3] > s[2])
                        throw new IllegalStateException("Invalid filesystem capacity");
                    buffer = prepareReply(buffer);
                    buffer.putByte((byte) SftpConstants.SSH_FXP_EXTENDED_REPLY); buffer.putInt(id);
                    for (long value : new long[]{s[0], s[0], s[1], s[2], s[3], 0, 0, 0, 0, 0, 255}) buffer.putLong(value);
                    send(buffer);
                } catch (RuntimeException e) {
                    sendStatus(prepareReply(buffer), id, SftpConstants.SSH_FX_FAILURE, "Filesystem capacity unavailable");
                }
            }
        };
        for (org.apache.sshd.sftp.server.SftpEventListener listener : getRegisteredListeners())
            subsystem.addSftpEventListener(listener);
        return subsystem;
    }
}
