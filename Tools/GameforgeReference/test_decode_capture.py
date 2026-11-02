"""Captured, non-authentication vectors and transport-integrity checks for the decoder."""
import struct
import unittest

from decode_capture import BASE, decrypt_packet, parse_item, reassemble, semantic


class CaptureDecoderTests(unittest.TestCase):
    def keys(self, index, body, header):
        bodies, headers = [0]*9, [0]*9
        bodies[index], headers[index] = body, header
        return bodies, headers

    def test_captured_server_eye_completion_vector(self):
        wire = bytes.fromhex('1e0014057d31092408422345084508456dada240779ddad86daa8940779d')
        keys = self.keys(5, 0xd8da9d774089aa6d, 0x23eb)
        ident, body = decrypt_packet(wire, 5492, 's2c', *keys)
        self.assertEqual((0x211d, bytes.fromhex('00072b0000000000000000000000')), (ident,body))
        self.assertEqual(11015,semantic(ident,body,'s2c')['quest_id'])

    def test_captured_client_quest_request_vector(self):
        wire = bytes.fromhex('19001f0524240424026e06450645064569818940779ddad86d')
        keys = self.keys(5, 0xd8da9d774089aa6d, 0x23eb)
        ident, body = decrypt_packet(wire, 32, 'c2s', *keys)
        self.assertEqual((0x211b, bytes.fromhex('042b00000000000000')), (ident,body))

    def test_header_only_packet_requires_correct_sequence(self):
        wire = bytes.fromhex('100049dc9afc98fc591d591d591d591d')
        keys = self.keys(2, 0x16d4ead19d600dba, 0xfc88)
        self.assertEqual((0x20d1,b''),decrypt_packet(wire,2,'c2s',*keys))
        with self.assertRaisesRegex(ValueError,'sequence mismatch'):
            decrypt_packet(wire,11,'c2s',*keys)

    def test_corrupted_body_fails_checksum(self):
        wire = bytearray.fromhex('1e0014057d31092408422345084508456dada240779ddad86daa8940779d')
        wire[-1] ^= 1
        with self.assertRaisesRegex(ValueError,'Checksum mismatch'):
            decrypt_packet(wire,5492,'s2c',*self.keys(5,0xd8da9d774089aa6d,0x23eb))

    def segment(self, seq, payload, frame=1):
        return dict(sequence=seq,payload=payload,frame=frame,time=float(frame))

    def test_out_of_order_and_overlapping_retransmit_preserve_exact_stream(self):
        raw,ranges,overlap=reassemble([self.segment(104,b'def',3),self.segment(101,b'abc',1),self.segment(103,b'cde',2)],100)
        self.assertEqual(b'abcdef',raw)
        self.assertEqual(3,overlap)
        self.assertEqual(6,ranges[-1]['end'])

    def test_tcp_sequence_wrap(self):
        raw,_,_=reassemble([self.segment(0xffffffff,b'a'),self.segment(0,b'bc',2)],0xfffffffe)
        self.assertEqual(b'abc',raw)

    def test_tcp_gap_is_not_filled_with_invented_bytes(self):
        with self.assertRaisesRegex(ValueError,'capture gap'):
            reassemble([self.segment(101,b'ab'),self.segment(104,b'e')],100)

    def test_conflicting_tcp_retransmission_is_rejected(self):
        with self.assertRaisesRegex(ValueError,'Conflicting TCP'):
            reassemble([self.segment(101,b'ab'),self.segment(102,b'X',2)],100)

    def test_actual_eye_item_layout_and_count(self):
        body=bytes.fromhex('ff0972257225000100000000000000000000000000000000000000000000000000000000')
        item=parse_item(body)
        self.assertEqual((255,9,9586,9586,1),(item['inventory'],item['slot'],item['template_id'],item['appearance_id'],item['count']))
        self.assertEqual([],item['magic'])
        with self.assertRaisesRegex(ValueError,'magic count'):
            parse_item(body+b'\0')

    def test_original_npc_collect_request_uses_dword_item_and_destination(self):
        body=struct.pack('<HIIBBB',26055,7930,9629,1,252,255)
        event=semantic(BASE+0x84,body,'c2s')
        self.assertEqual((26055,7930,9629,1,252,255),tuple(event[k] for k in ('npc_id','quest_id','item_id','count','destination_inventory','destination_slot')))

    def test_exp_and_soul_are_independent_original_messages(self):
        exp=semantic(BASE+0x24,struct.pack('<III',3811,2505,4363),'s2c')
        soul=semantic(0x2355,struct.pack('<III',124,57,0),'s2c')
        self.assertEqual(3811,exp['total'])
        self.assertNotIn('soul',exp)
        self.assertEqual((124,57),(soul['capacity'],soul['total']))
        self.assertIsNone(semantic(BASE+0x24,struct.pack('<IIII',3811,2505,4363,57),'s2c'))

    def test_quest_auxiliary_byte_is_not_invented_completion_cap(self):
        event=semantic(BASE+0x50,struct.pack('<IBB',11012,8,0),'s2c')
        self.assertEqual(0,event['auxiliary_byte'])
        self.assertNotIn('completion_cap',event)

    def test_npc_offer_identity_and_condition_are_read_exactly(self):
        body=struct.pack('<BHBBIB',1,26026,3,1,11015,0)
        event=semantic(BASE+0x5a,body,'s2c')
        self.assertEqual([dict(npc_id=26026,country=3,quests=[dict(quest_id=11015,condition=0)])],event['npcs'])
        with self.assertRaisesRegex(ValueError,'extra bytes'):
            semantic(BASE+0x5a,body+b'\0','s2c')

    def test_truncated_npc_offer_does_not_invent_missing_quest(self):
        with self.assertRaisesRegex(ValueError,'Truncated NPC quest entry'):
            semantic(BASE+0x5a,struct.pack('<BHBB',1,26026,3,1),'s2c')


if __name__ == '__main__':
    unittest.main()
