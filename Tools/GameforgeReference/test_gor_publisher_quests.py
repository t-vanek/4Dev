import unittest

from audit_gor_publisher_quests import compare, detail_facts


def page(rewards='', final='', giver=10, receiver=20):
    return f'''<h1>Testovací mise</h1><div id="tabs-1">
      <p class="questlevel">Rozsah: <strong>95</strong></p>
      <p class="questgiver"><a href="https://cz.4story.gameforge.com/npc/details/{giver}">NPC</a></p>
      <div class="detail_right"><ul><li>Cíl</li></ul><hr/><ul>{rewards}</ul></div></div>
      <div id="tabs-2"><cite><a href ="/npc/details/{giver}">NPC</a>:</cite>
      <blockquote>Začátek</blockquote><cite><a href='/npc/details/{receiver}'>NPC</a>:</cite>
      <blockquote>{final}</blockquote></div><div id="tabs-3"><ul class="tree">
      <li><a href="/quest/details/1">První</a><ul><li><a href="/quest/details/2">Další</a></li></ul></li></ul></div>
      <!-- sub-layout: end --><cite><a href="/npc/details/999">Nesouvisející</a></cite>'''


class PublisherQuestTests(unittest.TestCase):
    def test_giver_and_last_speaker_are_scoped_to_quest_description(self):
        facts = detail_facts(page())
        self.assertEqual(10, facts['giver_id'])
        self.assertEqual(20, facts['inferred_completion_npc_id'])
        self.assertEqual([10, 20], facts['dialogue_speaker_ids'])
        self.assertEqual([(1, 2)], facts['chain_edges'])

    def test_empty_published_rewards_are_not_converted_to_zero(self):
        facts = detail_facts(page(final='Dostaneš odměnu.'))
        self.assertIsNone(facts['money'])
        self.assertIsNone(facts['experience'])
        self.assertEqual([], facts['reward_items'])
        ref = dict(rewards=[dict(bRewardType=9, dwRewardID=167, bTakeMethod=1)])
        self.assertEqual([], compare(ref, facts))

    def test_explicit_talk_target_takes_priority_over_giver_only_dialogue(self):
        markup = page(receiver=10).replace('<li>Cíl</li>', '<li>Mluvit s <a href="/npc/details/30">NPC</a></li>')
        facts = detail_facts(markup)
        self.assertEqual(30, facts['inferred_completion_npc_id'])
        self.assertEqual([30], facts['objective_npc_ids'])
        self.assertEqual('Explicit single talk-objective NPC link.', facts['completion_npc_inference'])

    def test_actual_numeric_rewards_are_parsed_and_compared(self):
        facts = detail_facts(page('<li>Zkušenost: 855 Exp</li><li>0<span class="gold"></span>1<span class="silver"></span>782<span class="bronze"></span></li>'))
        self.assertEqual(855, facts['experience'])
        self.assertEqual(1782, facts['money'])
        ref = dict(rewards=[dict(bRewardType=t, dwRewardID=i, bTakeMethod=1) for t, i in [(1,1782),(6,855),(9,167)]])
        self.assertEqual([], compare(ref, facts))

    def test_conflicting_arena_points_are_reported_without_selecting_a_version(self):
        facts = detail_facts(page(final='Zde máš 100 bodů úspěchu.'))
        ref = dict(rewards=[dict(bRewardType=10, dwRewardID=150, bTakeMethod=1)])
        self.assertEqual([dict(field='achievement_points', publisher_completion_text=[100], pinned_amounts=[150])], compare(ref, facts))

    def test_landing_and_incomplete_pages_are_rejected(self):
        for markup in ('<h1>Úkoly v Iberii</h1>', page().replace('class="questgiver"', 'class="absent"')):
            with self.assertRaises(ValueError):
                detail_facts(markup)


if __name__ == '__main__':
    unittest.main()
